using AgentPulse.Domain.Messages;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Infrastructure.Tests.Persistence;

internal static class PersistenceTestData
{
    public static DateTime TimestampUtc { get; } =
        new(2026, 6, 27, 10, 0, 0, DateTimeKind.Utc);

    public static Project CreateProject(string? path = null)
    {
        return new Project(
            ProjectId.New(),
            path ?? $"/workspace/{Guid.NewGuid():N}",
            false,
            null,
            TimestampUtc);
    }

    public static Session CreateSession(Project project)
    {
        return new Session(SessionId.New(), project.Id, TimestampUtc);
    }

    public static Message CreateMessage(Session session, long sequence, string text)
    {
        var message = new Message(
            MessageId.New(),
            session.Id,
            MessageRole.User,
            sequence,
            TimestampUtc);
        message.AddTextPart(MessagePartId.New(), 1, text, TimestampUtc);
        message.Complete(TimestampUtc.AddSeconds(1));
        return message;
    }
}
