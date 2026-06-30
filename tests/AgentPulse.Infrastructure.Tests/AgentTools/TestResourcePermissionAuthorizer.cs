using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Permissions;

namespace AgentPulse.Infrastructure.Tests.AgentTools;

internal sealed class TestResourcePermissionAuthorizer(
    Func<string, PermissionAuthorizationResult>? authorize = null)
    : IAgentToolResourcePermissionAuthorizer
{
    public List<string> Targets { get; } = [];

    public Task<PermissionAuthorizationResult> AuthorizeAsync(
        string operation,
        string target,
        string? description,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Targets.Add(target);
        return Task.FromResult(authorize?.Invoke(target) ?? PermissionAuthorizationResult.Allow());
    }
}
