using AgentPulse.Application.Persistence;
using AgentPulse.Domain.Messages;
using AgentPulse.Domain.Sessions;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Persistence.Repositories;

public sealed class MessageRepository(AgentPulseDbContext dbContext) : IMessageRepository
{
    public Task<Message?> GetByIdAsync(
        MessageId id,
        CancellationToken cancellationToken = default)
    {
        return dbContext.Messages
            .Include(message => message.Parts.OrderBy(part => part.Order))
            .SingleOrDefaultAsync(message => message.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Message>> ListBySessionIdAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.Messages
            .Where(message => message.SessionId == sessionId)
            .Include(message => message.Parts.OrderBy(part => part.Order))
            .OrderBy(message => message.Sequence)
            .ThenBy(message => message.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Message message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await dbContext.Messages.AddAsync(message, cancellationToken);
    }

    public void Remove(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        dbContext.Messages.Remove(message);
    }
}
