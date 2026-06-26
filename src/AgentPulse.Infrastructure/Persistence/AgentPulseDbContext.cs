using AgentPulse.Domain.Messages;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;
using AgentPulse.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Persistence;

public sealed class AgentPulseDbContext(DbContextOptions<AgentPulseDbContext> options)
    : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<MessagePart> MessageParts => Set<MessagePart>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(SqliteForeignKeysInterceptor.Instance);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgentPulseDbContext).Assembly);
    }
}
