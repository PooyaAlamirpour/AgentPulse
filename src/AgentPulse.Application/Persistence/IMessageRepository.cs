using AgentPulse.Domain.Messages;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.Persistence;

public interface IMessageRepository
{
    Task<Message?> GetByIdAsync(MessageId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Message>> ListBySessionIdAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);

    Task AddAsync(Message message, CancellationToken cancellationToken = default);

    void Remove(Message message);
}
