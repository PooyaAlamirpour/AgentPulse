namespace AgentPulse.Application.Permissions;

public interface IPermissionApprovalPrompt
{
    bool IsInteractive { get; }

    Task<PermissionApprovalChoice> RequestApprovalAsync(
        PermissionRequest request,
        CancellationToken cancellationToken);
}
