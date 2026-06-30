using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.Permissions;

public interface IPermissionGate
{
    Task<PermissionAuthorizationResult> AuthorizeAsync(
        IAgentTool tool,
        JsonElement arguments,
        AgentToolExecutionContext toolContext,
        SessionId? sessionId,
        ProjectId? projectId,
        CancellationToken cancellationToken);

    Task<PermissionAuthorizationResult> AuthorizeAsync(
        IAgentTool tool,
        JsonElement arguments,
        AgentToolExecutionContext toolContext,
        SessionId? sessionId,
        ProjectId? projectId,
        PermissionAuthorizationContext authorizationContext,
        CancellationToken cancellationToken);

    Task<PermissionAuthorizationResult> AuthorizeResourceAsync(
        IAgentTool tool,
        string operation,
        string target,
        string? description,
        AgentToolExecutionContext toolContext,
        SessionId? sessionId,
        ProjectId? projectId,
        PermissionAuthorizationContext authorizationContext,
        CancellationToken cancellationToken);
}
