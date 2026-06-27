using AgentPulse.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Tests.Persistence;

public sealed class DesignTimeDbContextFactoryTests
{
    [Fact]
    public async Task Missing_override_uses_absolute_temporary_database_outside_repository()
    {
        var root = CreateTemporaryRoot("AgentPulse Design Time Tests");
        var repositoryDirectory = Path.Combine(root, "repository");
        var designTimeDirectory = Path.Combine(root, "design-time");
        Directory.CreateDirectory(repositoryDirectory);

        try
        {
            var factory = new AgentPulseDbContextFactory(
                configuredDatabasePath: null,
                designTimeDirectory);
            await using var context = factory.CreateDbContext([]);
            var builder = new SqliteConnectionStringBuilder(
                context.Database.GetConnectionString());

            Assert.True(Path.IsPathFullyQualified(builder.DataSource));
            Assert.Equal(
                Path.Combine(designTimeDirectory, "agentpulse-migrations.db"),
                builder.DataSource);
            Assert.False(IsWithinDirectory(builder.DataSource, repositoryDirectory));

            await context.Database.MigrateAsync();
            Assert.True(File.Exists(builder.DataSource));
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task Absolute_override_with_spaces_is_accepted()
    {
        var root = CreateTemporaryRoot("AgentPulse Design Time Override Tests");
        var databasePath = Path.Combine(root, "custom database", "migration file.db");

        try
        {
            var factory = new AgentPulseDbContextFactory(
                databasePath,
                Path.Combine(root, "fallback"));
            await using var context = factory.CreateDbContext([]);
            var builder = new SqliteConnectionStringBuilder(
                context.Database.GetConnectionString());

            Assert.Equal(databasePath, builder.DataSource);

            await context.Database.MigrateAsync();
            Assert.True(File.Exists(databasePath));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void Relative_override_is_rejected_without_creating_database_in_current_directory()
    {
        var root = CreateTemporaryRoot("AgentPulse Relative Design Time Tests");
        var relativeFileName = $"agentpulse-{Guid.NewGuid():N}.db";
        var currentDirectoryDatabase = Path.Combine(Directory.GetCurrentDirectory(), relativeFileName);

        try
        {
            var factory = new AgentPulseDbContextFactory(
                relativeFileName,
                Path.Combine(root, "fallback"));

            var exception = Assert.Throws<InvalidOperationException>(
                () => factory.CreateDbContext([]));

            Assert.Equal(
                "AgentPulse__Persistence__DatabasePath must be an absolute path when used at design time.",
                exception.Message);
            Assert.False(File.Exists(currentDirectoryDatabase));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void Design_time_factory_has_no_credential_store_dependency()
    {
        var dependencyTypes = typeof(AgentPulseDbContextFactory)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Concat(typeof(AgentPulseDbContextFactory)
                .GetFields(System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                .Select(field => field.FieldType))
            .ToArray();

        Assert.DoesNotContain(
            dependencyTypes,
            type => type.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateTemporaryRoot(string category)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            category,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var relativePath = Path.GetRelativePath(directory, path);
        return !relativePath.Equals("..", StringComparison.Ordinal) &&
            !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relativePath);
    }

    private static void DeleteTemporaryRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
