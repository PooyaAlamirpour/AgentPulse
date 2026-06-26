using AgentPulse.Domain.Messages;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;
using AgentPulse.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Persistence.Migrations;

internal static class InitialDomainPersistenceModel
{
    public static void Build(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "8.0.11");

        modelBuilder.Entity<Project>(builder =>
        {
            builder.ToTable("Projects");
            builder.HasKey(project => project.Id);

            builder.Property(project => project.Id)
                .HasConversion<ProjectIdConverter>()
                .HasColumnType("TEXT")
                .ValueGeneratedNever();

            builder.Property(project => project.NormalizedRootPath)
                .HasColumnType("TEXT")
                .HasMaxLength(4096)
                .IsRequired();

            builder.Property(project => project.IsGitRepository)
                .HasColumnType("INTEGER")
                .IsRequired();

            builder.Property(project => project.GitWorktree)
                .HasColumnType("TEXT")
                .HasMaxLength(4096);

            builder.Property(project => project.CreatedAtUtc)
                .HasConversion<UtcDateTimeConverter>()
                .HasColumnType("INTEGER")
                .IsRequired();

            builder.Property(project => project.UpdatedAtUtc)
                .HasConversion<UtcDateTimeConverter>()
                .HasColumnType("INTEGER")
                .IsRequired();

            builder.HasIndex(project => project.NormalizedRootPath)
                .IsUnique();
        });

        modelBuilder.Entity<Session>(builder =>
        {
            builder.ToTable("Sessions");
            builder.HasKey(session => session.Id);

            builder.Property(session => session.Id)
                .HasConversion<SessionIdConverter>()
                .HasColumnType("TEXT")
                .ValueGeneratedNever();

            builder.Property(session => session.ProjectId)
                .HasConversion<ProjectIdConverter>()
                .HasColumnType("TEXT")
                .IsRequired();

            builder.Property(session => session.Status)
                .HasConversion<string>()
                .HasColumnType("TEXT")
                .HasMaxLength(16)
                .IsRequired();

            builder.Property(session => session.CreatedAtUtc)
                .HasConversion<UtcDateTimeConverter>()
                .HasColumnType("INTEGER")
                .IsRequired();

            builder.Property(session => session.UpdatedAtUtc)
                .HasConversion<UtcDateTimeConverter>()
                .HasColumnType("INTEGER")
                .IsRequired();

            builder.HasIndex(session => session.ProjectId);

            builder.HasOne<Project>()
                .WithMany()
                .HasForeignKey(session => session.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
        });

        modelBuilder.Entity<Message>(builder =>
        {
            builder.ToTable("Messages");
            builder.HasKey(message => message.Id);

            builder.Property(message => message.Id)
                .HasConversion<MessageIdConverter>()
                .HasColumnType("TEXT")
                .ValueGeneratedNever();

            builder.Property(message => message.SessionId)
                .HasConversion<SessionIdConverter>()
                .HasColumnType("TEXT")
                .IsRequired();

            builder.Property(message => message.Role)
                .HasConversion<string>()
                .HasColumnType("TEXT")
                .HasMaxLength(16)
                .IsRequired();

            builder.Property(message => message.Status)
                .HasConversion<string>()
                .HasColumnType("TEXT")
                .HasMaxLength(16)
                .IsRequired();

            builder.Property(message => message.Sequence)
                .HasColumnType("INTEGER")
                .IsRequired();

            builder.Property(message => message.CreatedAtUtc)
                .HasConversion<UtcDateTimeConverter>()
                .HasColumnType("INTEGER")
                .IsRequired();

            builder.Property(message => message.UpdatedAtUtc)
                .HasConversion<UtcDateTimeConverter>()
                .HasColumnType("INTEGER")
                .IsRequired();

            builder.HasIndex(message => new { message.SessionId, message.Sequence })
                .IsUnique();

            builder.HasOne<Session>()
                .WithMany()
                .HasForeignKey(message => message.SessionId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();

            builder.HasMany(message => message.Parts)
                .WithOne()
                .HasForeignKey(part => part.MessageId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Navigation(message => message.Parts)
                .UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<MessagePart>(builder =>
        {
            builder.ToTable("MessageParts");
            builder.HasKey(part => part.Id);

            builder.Property(part => part.Id)
                .HasConversion<MessagePartIdConverter>()
                .HasColumnType("TEXT")
                .ValueGeneratedNever();

            builder.Property(part => part.MessageId)
                .HasConversion<MessageIdConverter>()
                .HasColumnType("TEXT")
                .IsRequired();

            builder.Property(part => part.Order)
                .HasColumnType("INTEGER")
                .IsRequired();

            builder.Property(part => part.CreatedAtUtc)
                .HasConversion<UtcDateTimeConverter>()
                .HasColumnType("INTEGER")
                .IsRequired();

            builder.Property(part => part.UpdatedAtUtc)
                .HasConversion<UtcDateTimeConverter>()
                .HasColumnType("INTEGER")
                .IsRequired();

            builder.Property<string>("PartType")
                .HasColumnType("TEXT")
                .HasMaxLength(32)
                .IsRequired();

            builder.HasIndex(part => new { part.MessageId, part.Order })
                .IsUnique();

            builder.HasDiscriminator<string>("PartType")
                .HasValue<TextMessagePart>("text")
                .IsComplete(false);
        });

        modelBuilder.Entity<TextMessagePart>(builder =>
        {
            builder.HasBaseType<MessagePart>();
            builder.Property(part => part.Text)
                .HasColumnType("TEXT")
                .IsRequired(false);
        });
    }
}
