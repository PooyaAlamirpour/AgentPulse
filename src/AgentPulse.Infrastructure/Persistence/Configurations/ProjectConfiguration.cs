using AgentPulse.Domain.Projects;
using AgentPulse.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentPulse.Infrastructure.Persistence.Configurations;

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("Projects");
        builder.HasKey(project => project.Id);

        builder.Property(project => project.Id)
            .HasConversion<ProjectIdConverter>()
            .ValueGeneratedNever();

        builder.Property(project => project.NormalizedRootPath)
            .HasMaxLength(4096)
            .IsRequired();

        builder.HasIndex(project => project.NormalizedRootPath)
            .IsUnique();

        builder.Property(project => project.IsGitRepository)
            .IsRequired();

        builder.Property(project => project.GitWorktree)
            .HasMaxLength(4096);

        builder.Property(project => project.CreatedAtUtc)
            .HasConversion<UtcDateTimeConverter>()
            .IsRequired();

        builder.Property(project => project.UpdatedAtUtc)
            .HasConversion<UtcDateTimeConverter>()
            .IsRequired();
    }
}
