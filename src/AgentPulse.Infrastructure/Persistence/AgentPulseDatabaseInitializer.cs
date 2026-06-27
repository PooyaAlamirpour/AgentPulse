using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Persistence;

public sealed class AgentPulseDatabaseInitializer(
    IDbContextFactory<AgentPulseDbContext> dbContextFactory)
    : IAgentPulseDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
