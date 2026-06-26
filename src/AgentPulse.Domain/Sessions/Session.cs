using AgentPulse.Domain.Common;
using AgentPulse.Domain.Projects;

namespace AgentPulse.Domain.Sessions;

public sealed class Session
{
    private Session()
    {
    }

    public Session(
        SessionId id,
        ProjectId projectId,
        DateTime createdAtUtc)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("Session identifier cannot be empty.", nameof(id));
        }

        if (projectId.Value == Guid.Empty)
        {
            throw new ArgumentException("Project identifier cannot be empty.", nameof(projectId));
        }

        Id = id;
        ProjectId = projectId;
        Status = SessionStatus.Idle;
        CreatedAtUtc = UtcDateTime.Ensure(createdAtUtc, nameof(createdAtUtc));
        UpdatedAtUtc = CreatedAtUtc;
    }

    public SessionId Id { get; private set; }

    public ProjectId ProjectId { get; private set; }

    public SessionStatus Status { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public void Start(DateTime updatedAtUtc)
    {
        if (Status == SessionStatus.Running)
        {
            throw new InvalidOperationException("Session is already running.");
        }

        var validatedUpdatedAtUtc = UtcDateTime.EnsureNotBefore(
            updatedAtUtc,
            CreatedAtUtc,
            nameof(updatedAtUtc));

        Status = SessionStatus.Running;
        UpdatedAtUtc = validatedUpdatedAtUtc;
    }

    public void Stop(DateTime updatedAtUtc)
    {
        if (Status == SessionStatus.Idle)
        {
            throw new InvalidOperationException("Session is already idle.");
        }

        var validatedUpdatedAtUtc = UtcDateTime.EnsureNotBefore(
            updatedAtUtc,
            CreatedAtUtc,
            nameof(updatedAtUtc));

        Status = SessionStatus.Idle;
        UpdatedAtUtc = validatedUpdatedAtUtc;
    }
}
