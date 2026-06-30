using System.Text.Json;
using AgentPulse.Application.Permissions;
using AgentPulse.Domain.Projects;
using AgentPulse.Infrastructure.Permissions;

namespace AgentPulse.Infrastructure.Tests.Permissions;

public sealed class ProjectPermissionStoreTests
{
    [Fact]
    public async Task Project_approval_survives_store_reload_and_is_project_scoped()
    {
        using var directory = new TemporaryDirectory();
        var project = ProjectId.New();
        var otherProject = ProjectId.New();
        var approval = PermissionApproval.Create("read", "read", "src/Program.cs");

        var firstStore = new JsonProjectPermissionStore(directory.Path);
        await firstStore.AddAsync(project, approval, CancellationToken.None);

        var reloadedStore = new JsonProjectPermissionStore(directory.Path);
        Assert.True(await reloadedStore.ContainsAsync(project, approval, CancellationToken.None));
        Assert.False(await reloadedStore.ContainsAsync(otherProject, approval, CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_approvals_are_not_stored_twice()
    {
        using var directory = new TemporaryDirectory();
        var project = ProjectId.New();
        var approval = PermissionApproval.Create("read", "read", "src/Program.cs");
        var store = new JsonProjectPermissionStore(directory.Path);

        await store.AddAsync(project, approval, CancellationToken.None);
        await store.AddAsync(project, approval, CancellationToken.None);

        var path = Assert.Single(Directory.GetFiles(directory.Path, "*.permissions.json"));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Single(document.RootElement.GetProperty("approvals").EnumerateArray().ToArray());
    }

    [Fact]
    public async Task Corrupt_permission_data_returns_a_clear_error()
    {
        using var directory = new TemporaryDirectory();
        var project = ProjectId.New();
        var path = Path.Combine(
            directory.Path,
            project.Value.ToString("N") + ".permissions.json");
        await File.WriteAllTextAsync(path, "{ invalid");
        var store = new JsonProjectPermissionStore(directory.Path);

        var exception = await Assert.ThrowsAsync<PermissionStoreException>(() =>
            store.ContainsAsync(
                project,
                PermissionApproval.Create("read", "read", "src/Program.cs"),
                CancellationToken.None));

        Assert.Contains("contains invalid JSON", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(directory.Path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_writes_do_not_corrupt_storage()
    {
        using var directory = new TemporaryDirectory();
        var project = ProjectId.New();
        var stores = Enumerable.Range(0, 4)
            .Select(_ => new JsonProjectPermissionStore(directory.Path))
            .ToArray();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            stores[index % stores.Length].AddAsync(
                project,
                PermissionApproval.Create("read", "read", $"src/File{index}.cs"),
                CancellationToken.None)));

        var verifier = new JsonProjectPermissionStore(directory.Path);
        for (var index = 0; index < 20; index++)
        {
            Assert.True(await verifier.ContainsAsync(
                project,
                PermissionApproval.Create("read", "read", $"src/File{index}.cs"),
                CancellationToken.None));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "agentpulse-permission-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
