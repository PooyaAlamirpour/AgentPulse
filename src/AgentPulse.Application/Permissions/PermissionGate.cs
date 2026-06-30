using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;
using Microsoft.Extensions.Logging;

namespace AgentPulse.Application.Permissions;

public sealed class PermissionGate(
    IPermissionRuleEvaluator evaluator,
    ISessionPermissionStore sessionStore,
    IProjectPermissionStore projectStore,
    IPermissionApprovalPrompt approvalPrompt,
    PermissionOptions options,
    ILogger<PermissionGate> logger) : IPermissionGate
{
    private readonly IReadOnlyList<PermissionRule> _rules = options.CreateRules();
    private readonly PermissionDecision _defaultDecision = options.GetDefaultDecision();

    public Task<PermissionAuthorizationResult> AuthorizeAsync(
        IAgentTool tool,
        JsonElement arguments,
        AgentToolExecutionContext toolContext,
        SessionId? sessionId,
        ProjectId? projectId,
        CancellationToken cancellationToken)
    {
        return AuthorizeAsync(
            tool,
            arguments,
            toolContext,
            sessionId,
            projectId,
            new PermissionAuthorizationContext(),
            cancellationToken);
    }

    public async Task<PermissionAuthorizationResult> AuthorizeAsync(
        IAgentTool tool,
        JsonElement arguments,
        AgentToolExecutionContext toolContext,
        SessionId? sessionId,
        ProjectId? projectId,
        PermissionAuthorizationContext authorizationContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(toolContext);
        ArgumentNullException.ThrowIfNull(authorizationContext);

        if (tool is not IPermissionAwareAgentTool permissionAwareTool)
        {
            return UnclassifiedTool(tool.Name);
        }

        var targetResolution = permissionAwareTool.ResolvePermissionTarget(arguments, toolContext);
        if (!targetResolution.Succeeded)
        {
            return PermissionAuthorizationResult.Reject(
                targetResolution.Failure ?? AgentToolResult.Failure("Permission target validation failed."));
        }

        return await AuthorizeRequestAsync(
            tool.Name,
            targetResolution.Operation!,
            targetResolution.Target!,
            targetResolution.Description,
            toolContext,
            sessionId,
            projectId,
            authorizationContext,
            ResolveDefaultDecision(tool),
            isResourceLevel: false,
            cancellationToken);
    }

    public Task<PermissionAuthorizationResult> AuthorizeResourceAsync(
        IAgentTool tool,
        string operation,
        string target,
        string? description,
        AgentToolExecutionContext toolContext,
        SessionId? sessionId,
        ProjectId? projectId,
        PermissionAuthorizationContext authorizationContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(toolContext);
        ArgumentNullException.ThrowIfNull(authorizationContext);

        if (tool is not IPermissionAwareAgentTool)
        {
            return Task.FromResult(UnclassifiedTool(tool.Name));
        }

        return AuthorizeRequestAsync(
            tool.Name,
            operation,
            target,
            description,
            toolContext,
            sessionId,
            projectId,
            authorizationContext,
            ResolveDefaultDecision(tool),
            isResourceLevel: true,
            cancellationToken);
    }

    private async Task<PermissionAuthorizationResult> AuthorizeRequestAsync(
        string toolName,
        string operation,
        string target,
        string? description,
        AgentToolExecutionContext toolContext,
        SessionId? sessionId,
        ProjectId? projectId,
        PermissionAuthorizationContext authorizationContext,
        PermissionDecision defaultDecision,
        bool isResourceLevel,
        CancellationToken cancellationToken)
    {
        if (sessionId is null || projectId is null)
        {
            return PermissionAuthorizationResult.Reject(AgentToolResult.Failure(
                "Permission context is unavailable for this tool call."));
        }

        PermissionRequest request;
        try
        {
            request = new PermissionRequest(
                toolName,
                operation,
                target,
                toolContext.WorkspaceRoot,
                sessionId.Value,
                projectId.Value,
                approvalPrompt.IsInteractive,
                description);
        }
        catch (ArgumentException exception)
        {
            return PermissionAuthorizationResult.Reject(AgentToolResult.Failure(exception.Message));
        }

        logger.LogDebug(
            "Permission evaluation started for tool {ToolName}, operation {Operation}, and target {Target}.",
            request.ToolName,
            request.Operation,
            request.Target);
        PermissionEvaluationResult evaluation;
        try
        {
            evaluation = evaluator.Evaluate(request, _rules, defaultDecision);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Permission evaluation failed for tool {ToolName}.", request.ToolName);
            return PermissionAuthorizationResult.Reject(
                AgentToolResult.Failure($"Permission evaluation failed: {exception.Message}"),
                status: PermissionAuthorizationStatus.InfrastructureFailure);
        }
        var maximumApprovalScope = evaluation.Decision == PermissionDecision.Ask
            ? evaluation.MatchedRule?.Scope ?? PermissionScope.Project
            : PermissionScope.Project;
        request = request.WithMaximumApprovalScope(maximumApprovalScope);

        logger.LogDebug(
            "Permission decision {Decision} resolved from source {RuleSource} for tool {ToolName}.",
            evaluation.Decision,
            evaluation.RuleSource,
            request.ToolName);
        if (isResourceLevel)
        {
            logger.LogDebug(
                "Resource-level permission evaluated for tool {ToolName}, operation {Operation}, and target {Target} with decision {Decision}.",
                request.ToolName,
                request.Operation,
                request.Target,
                evaluation.Decision);
        }

        if (evaluation.MatchedRule is not null)
        {
            logger.LogDebug(
                "Permission matching rule selected with source {RuleSource}, scope {Scope}, and decision {Decision}.",
                evaluation.MatchedRule.Source,
                evaluation.MatchedRule.Scope,
                evaluation.MatchedRule.Decision);
        }

        if (evaluation.Decision == PermissionDecision.Allow)
        {
            return PermissionAuthorizationResult.Allow(evaluation);
        }

        if (evaluation.Decision == PermissionDecision.Deny)
        {
            logger.LogWarning(
                isResourceLevel
                    ? "Candidate path denied for tool {ToolName} and target {Target}."
                    : "Permission denied for tool {ToolName} and target {Target}.",
                request.ToolName,
                request.Target);
            return Denied(request, evaluation, evaluation.RuleSource);
        }

        if (authorizationContext.TryGetDecision(
                request,
                evaluation,
                isResourceLevel,
                out var transientDecision))
        {
            if (transientDecision == PermissionDecision.Allow)
            {
                return PermissionAuthorizationResult.Allow(ResolveApproval(
                    request,
                    evaluation,
                    PermissionRuleSource.OneTimeApproval,
                    PermissionScope.Once,
                    "A matching approval for the current tool call allowed the permission request."));
            }

            return Denied(
                request,
                evaluation,
                PermissionRuleSource.UserDecision,
                "A matching denial for the current tool call denied the permission request.");
        }

        var approval = CreateApproval(request, evaluation, isResourceLevel);
        try
        {
            var sessionApprovalExists = await sessionStore.ContainsAsync(
                request.SessionId,
                approval,
                cancellationToken);
            if (sessionApprovalExists)
            {
                if (IsScopeAllowed(PermissionScope.Session, maximumApprovalScope))
                {
                    authorizationContext.RecordDecision(
                        request,
                        evaluation,
                        isResourceLevel,
                        PermissionDecision.Allow);
                    logger.LogDebug("Session permission approval matched for tool {ToolName}.", request.ToolName);
                    return PermissionAuthorizationResult.Allow(ResolveApproval(
                        request,
                        evaluation,
                        PermissionRuleSource.SessionApproval,
                        PermissionScope.Session,
                        "A matching session approval allowed the permission request."));
                }

                logger.LogWarning(
                    "Stale persisted approval ignored because session scope exceeds rule scope {RuleScope} for tool {ToolName} and target {Target}.",
                    maximumApprovalScope,
                    request.ToolName,
                    request.Target);
            }

            var projectApprovalExists = await projectStore.ContainsAsync(
                request.ProjectId,
                approval,
                cancellationToken);
            if (projectApprovalExists)
            {
                if (IsScopeAllowed(PermissionScope.Project, maximumApprovalScope))
                {
                    authorizationContext.RecordDecision(
                        request,
                        evaluation,
                        isResourceLevel,
                        PermissionDecision.Allow);
                    logger.LogDebug("Project permission approval matched for tool {ToolName}.", request.ToolName);
                    return PermissionAuthorizationResult.Allow(ResolveApproval(
                        request,
                        evaluation,
                        PermissionRuleSource.ProjectApproval,
                        PermissionScope.Project,
                        "A matching project approval allowed the permission request."));
                }

                logger.LogWarning(
                    "Stale persisted approval ignored because project scope exceeds rule scope {RuleScope} for tool {ToolName} and target {Target}.",
                    maximumApprovalScope,
                    request.ToolName,
                    request.Target);
            }
        }
        catch (PermissionStoreException exception)
        {
            logger.LogError(exception, "Permission approval data could not be loaded safely.");
            return PermissionAuthorizationResult.Reject(AgentToolResult.Failure(exception.Message));
        }

        if (!request.IsInteractive)
        {
            logger.LogWarning("Permission approval is unavailable in non-interactive mode for tool {ToolName}.", request.ToolName);
            var nonInteractiveResolution = new PermissionEvaluationResult(
                PermissionDecision.Deny,
                evaluation.MatchedRule,
                request.ToolName,
                request.Operation,
                request.Target,
                "The permission request required approval in a non-interactive run.",
                PermissionRuleSource.NonInteractivePolicy,
                null);
            return PermissionAuthorizationResult.Reject(
                AgentToolResult.Failure(
                    "Permission approval is required, but the current run is non-interactive."),
                nonInteractiveResolution,
                PermissionAuthorizationStatus.ApprovalUnavailable);
        }

        if (isResourceLevel)
        {
            logger.LogInformation(
                "Candidate path requires approval for tool {ToolName} and target {Target}.",
                request.ToolName,
                request.Target);
        }
        else
        {
            logger.LogInformation("Permission approval requested for tool {ToolName}.", request.ToolName);
        }

        PermissionApprovalChoice choice;
        try
        {
            choice = await approvalPrompt.RequestApprovalAsync(
                request.WithTarget(approval.Target),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Permission approval was cancelled for tool {ToolName}.", request.ToolName);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Permission approval failed for tool {ToolName}.", request.ToolName);
            return PermissionAuthorizationResult.Reject(
                AgentToolResult.Failure($"Permission approval failed: {exception.Message}"),
                status: PermissionAuthorizationStatus.InfrastructureFailure);
        }

        if (choice == PermissionApprovalChoice.Unavailable)
        {
            logger.LogWarning(
                "Permission approval was unavailable for tool {ToolName} and target {Target}.",
                request.ToolName,
                request.Target);
            return PermissionAuthorizationResult.Reject(
                AgentToolResult.Failure(
                    "Permission approval is unavailable because the input stream reached EOF."),
                status: PermissionAuthorizationStatus.ApprovalUnavailable);
        }

        if (!TryGetApprovalScope(choice, out var selectedScope))
        {
            if (choice != PermissionApprovalChoice.Deny)
            {
                authorizationContext.RecordDecision(
                    request,
                    evaluation,
                    isResourceLevel,
                    PermissionDecision.Deny);
                return PermissionAuthorizationResult.Reject(
                    AgentToolResult.Failure(
                        $"Permission approval response '{choice}' is invalid. Execution was denied."),
                    status: PermissionAuthorizationStatus.InvalidApproval);
            }
        }
        else if (!IsScopeAllowed(selectedScope, maximumApprovalScope))
        {
            logger.LogWarning(
                "Approval rejected because it exceeds rule scope {RuleScope}. Requested scope {RequestedScope} for tool {ToolName} and target {Target}.",
                maximumApprovalScope,
                selectedScope,
                request.ToolName,
                request.Target);
            authorizationContext.RecordDecision(
                request,
                evaluation,
                isResourceLevel,
                PermissionDecision.Deny);
            return PermissionAuthorizationResult.Reject(
                AgentToolResult.Failure(
                    $"Permission approval '{choice}' exceeds the configured {maximumApprovalScope} scope for tool '{request.ToolName}' on '{request.Target}'. Execution was denied."),
                status: PermissionAuthorizationStatus.InvalidApproval);
        }

        switch (choice)
        {
            case PermissionApprovalChoice.AllowOnce:
                authorizationContext.RecordDecision(
                    request,
                    evaluation,
                    isResourceLevel,
                    PermissionDecision.Allow);
                logger.LogInformation("Allow once selected for tool {ToolName}.", request.ToolName);
                return PermissionAuthorizationResult.Allow(ResolveApproval(
                    request,
                    evaluation,
                    PermissionRuleSource.OneTimeApproval,
                    PermissionScope.Once,
                    "The user allowed this permission request once."));
            case PermissionApprovalChoice.AllowSession:
                try
                {
                    await sessionStore.AddAsync(request.SessionId, approval, cancellationToken);
                }
                catch (PermissionStoreException exception)
                {
                    logger.LogError(exception, "Session permission approval could not be stored safely.");
                    return PermissionAuthorizationResult.Reject(
                        AgentToolResult.Failure(exception.Message),
                        status: PermissionAuthorizationStatus.InfrastructureFailure);
                }

                authorizationContext.RecordDecision(
                    request,
                    evaluation,
                    isResourceLevel,
                    PermissionDecision.Allow);
                logger.LogInformation("Session permission approval stored for tool {ToolName}.", request.ToolName);
                return PermissionAuthorizationResult.Allow(ResolveApproval(
                    request,
                    evaluation,
                    PermissionRuleSource.SessionApproval,
                    PermissionScope.Session,
                    "The user allowed this permission request for the current session."));
            case PermissionApprovalChoice.AllowProject:
                try
                {
                    await projectStore.AddAsync(request.ProjectId, approval, cancellationToken);
                }
                catch (PermissionStoreException exception)
                {
                    logger.LogError(exception, "Project permission approval could not be stored safely.");
                    return PermissionAuthorizationResult.Reject(
                        AgentToolResult.Failure(exception.Message),
                        status: PermissionAuthorizationStatus.InfrastructureFailure);
                }

                authorizationContext.RecordDecision(
                    request,
                    evaluation,
                    isResourceLevel,
                    PermissionDecision.Allow);
                logger.LogInformation("Project permission approval stored for tool {ToolName}.", request.ToolName);
                return PermissionAuthorizationResult.Allow(ResolveApproval(
                    request,
                    evaluation,
                    PermissionRuleSource.ProjectApproval,
                    PermissionScope.Project,
                    "The user allowed this permission request for the current project."));
            case PermissionApprovalChoice.Deny:
                authorizationContext.RecordDecision(
                    request,
                    evaluation,
                    isResourceLevel,
                    PermissionDecision.Deny);
                logger.LogWarning("Permission denied by the user for tool {ToolName}.", request.ToolName);
                return Denied(request, evaluation, PermissionRuleSource.UserDecision);
            default:
                return PermissionAuthorizationResult.Reject(
                    AgentToolResult.Failure(
                        "The permission approval response is invalid. Execution was denied."),
                    status: PermissionAuthorizationStatus.InvalidApproval);
        }
    }

    private PermissionDecision ResolveDefaultDecision(IAgentTool tool)
    {
        return tool is IAgentToolDefaultPermission toolDefault
            ? toolDefault.DefaultPermissionDecision
            : _defaultDecision;
    }

    private PermissionAuthorizationResult UnclassifiedTool(string toolName)
    {
        logger.LogWarning("Unclassified tool denied: {ToolName}.", toolName);
        return PermissionAuthorizationResult.Reject(AgentToolResult.Failure(
            $"Permission metadata is not defined for tool '{toolName}'. Execution was denied."));
    }

    private static PermissionApproval CreateApproval(
        PermissionRequest request,
        PermissionEvaluationResult evaluation,
        bool isResourceLevel)
    {
        var target = PermissionAuthorizationContext.GetDecisionTarget(
            request,
            evaluation,
            isResourceLevel);
        return PermissionApproval.Create(request.ToolName, request.Operation, target);
    }

    private static bool TryGetApprovalScope(
        PermissionApprovalChoice choice,
        out PermissionScope scope)
    {
        switch (choice)
        {
            case PermissionApprovalChoice.AllowOnce:
                scope = PermissionScope.Once;
                return true;
            case PermissionApprovalChoice.AllowSession:
                scope = PermissionScope.Session;
                return true;
            case PermissionApprovalChoice.AllowProject:
                scope = PermissionScope.Project;
                return true;
            default:
                scope = default;
                return false;
        }
    }

    private static bool IsScopeAllowed(
        PermissionScope requestedScope,
        PermissionScope maximumScope) => requestedScope <= maximumScope;

    private static PermissionAuthorizationResult Denied(
        PermissionRequest request,
        PermissionEvaluationResult evaluation,
        PermissionRuleSource source,
        string? reason = null)
    {
        var resolution = new PermissionEvaluationResult(
            PermissionDecision.Deny,
            evaluation.MatchedRule,
            request.ToolName,
            request.Operation,
            request.Target,
            reason ?? (source == PermissionRuleSource.UserDecision
                ? "The user denied the permission request."
                : evaluation.Reason),
            source,
            null);
        return PermissionAuthorizationResult.Reject(
            AgentToolResult.Failure(
                $"Permission denied for tool '{request.ToolName}' on '{request.Target}'."),
            resolution,
            PermissionAuthorizationStatus.ExplicitlyDenied);
    }

    private static PermissionEvaluationResult ResolveApproval(
        PermissionRequest request,
        PermissionEvaluationResult evaluation,
        PermissionRuleSource source,
        PermissionScope scope,
        string reason)
    {
        return new PermissionEvaluationResult(
            PermissionDecision.Allow,
            evaluation.MatchedRule,
            request.ToolName,
            request.Operation,
            request.Target,
            reason,
            source,
            scope);
    }
}
