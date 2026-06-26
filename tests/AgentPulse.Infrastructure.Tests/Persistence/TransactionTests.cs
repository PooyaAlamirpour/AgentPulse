using AgentPulse.Infrastructure.Persistence;
using AgentPulse.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Tests.Persistence;

public sealed class TransactionTests
{
    [Fact]
    public async Task Committed_multi_table_transaction_is_persisted_atomically()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var project = PersistenceTestData.CreateProject();
        var session = PersistenceTestData.CreateSession(project);
        var message = PersistenceTestData.CreateMessage(session, 1, "committed");

        await using (var context = database.CreateContext())
        {
            var unitOfWork = new UnitOfWork(context);
            await using var transaction = await unitOfWork.BeginTransactionAsync();

            await new ProjectRepository(context).AddAsync(project);
            await new SessionRepository(context).AddAsync(session);
            await new MessageRepository(context).AddAsync(message);
            await unitOfWork.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await using var verificationContext = database.CreateContext();
        Assert.Equal(1, await verificationContext.Projects.CountAsync());
        Assert.Equal(1, await verificationContext.Sessions.CountAsync());
        Assert.Equal(1, await verificationContext.Messages.CountAsync());
        Assert.Equal(1, await verificationContext.MessageParts.CountAsync());
    }

    [Fact]
    public async Task Rolled_back_transaction_persists_nothing()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var project = PersistenceTestData.CreateProject();
        var session = PersistenceTestData.CreateSession(project);

        await using (var context = database.CreateContext())
        {
            var unitOfWork = new UnitOfWork(context);
            await using var transaction = await unitOfWork.BeginTransactionAsync();

            await new ProjectRepository(context).AddAsync(project);
            await new SessionRepository(context).AddAsync(session);
            await unitOfWork.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        await using var verificationContext = database.CreateContext();
        Assert.Empty(await verificationContext.Projects.ToListAsync());
        Assert.Empty(await verificationContext.Sessions.ToListAsync());
    }
}
