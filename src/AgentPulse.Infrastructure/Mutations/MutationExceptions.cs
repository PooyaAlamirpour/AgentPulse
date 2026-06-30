using AgentPulse.Application.AgentTools;

namespace AgentPulse.Infrastructure.Mutations;

internal sealed class MutationValidationException(string message) : Exception(message)
{
}

internal sealed class MutationPermissionException(AgentToolResult failure)
    : Exception(failure.Error ?? "Mutation permission was denied.")
{
    public AgentToolResult Failure { get; } = failure;
}

internal sealed class MutationRollbackException(string message, Exception innerException)
    : Exception(message, innerException)
{
}
