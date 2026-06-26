using AgentPulse.Domain.Messages;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgentPulse.Infrastructure.Persistence.Converters;

internal sealed class ProjectIdConverter : ValueConverter<ProjectId, Guid>
{
    public ProjectIdConverter()
        : base(id => id.Value, value => new ProjectId(value))
    {
    }
}

internal sealed class SessionIdConverter : ValueConverter<SessionId, Guid>
{
    public SessionIdConverter()
        : base(id => id.Value, value => new SessionId(value))
    {
    }
}

internal sealed class MessageIdConverter : ValueConverter<MessageId, Guid>
{
    public MessageIdConverter()
        : base(id => id.Value, value => new MessageId(value))
    {
    }
}

internal sealed class MessagePartIdConverter : ValueConverter<MessagePartId, Guid>
{
    public MessagePartIdConverter()
        : base(id => id.Value, value => new MessagePartId(value))
    {
    }
}
