using System.Diagnostics;
using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.ChatModels;
using AgentPulse.Application.Permissions;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;
using Microsoft.Extensions.Logging;

namespace AgentPulse.Application.AgentLoop;

public sealed class AgentLoop(
    IChatModelClient chatModelClient,
    IAgentToolRegistry toolRegistry,
    AgentToolOptions options,
    ILogger<AgentLoop> logger,
    IPermissionGate? permissionGate = null) : IAgentLoop
{
    public async Task<AgentLoopResult> ExecuteAsync(
        AgentLoopRequest request,
        IAgentLoopObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Messages);
        options.Validate();
        observer ??= NullAgentLoopObserver.Instance;

        var messages = request.Messages.ToList();
        var definitions = toolRegistry.GetDefinitions();
        var context = new AgentToolExecutionContext(request.WorkspaceRoot);

        logger.LogInformation(
            "Agent loop started with {ToolCount} active tools and maximum {MaxIterations} iterations.",
            definitions.Count,
            options.MaxToolIterations);

        for (var iteration = 1; iteration <= options.MaxToolIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogDebug("Agent loop iteration {Iteration} started.", iteration);

            ChatModelResponse response;
            try
            {
                response = await chatModelClient.CompleteAsync(
                    new ChatModelRequest(messages, request.Model, definitions),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("Agent loop was cancelled during iteration {Iteration}.", iteration);
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Model provider failed during agent loop iteration {Iteration}.", iteration);
                throw new AgentLoopException(
                    AgentLoopErrorCode.ProviderFailure,
                    "The model provider failed during the agent loop.",
                    exception);
            }

            if (response.ToolCalls.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(response.Text))
                {
                    throw new AgentLoopException(
                        AgentLoopErrorCode.InvalidResponse,
                        "The model returned neither a final response nor a tool call.");
                }

                await observer.RecordAssistantResponseAsync(response, iteration, cancellationToken);
                messages.Add(new ChatModelMessage(ChatModelRole.Assistant, response.Text));
                logger.LogInformation("Agent loop completed after {IterationCount} iterations.", iteration);
                return new AgentLoopResult(
                    response.Text,
                    response.FinishReason,
                    response.Usage,
                    iteration);
            }

            await observer.RecordAssistantResponseAsync(response, iteration, cancellationToken);
            messages.Add(ChatModelMessage.CreateAssistantToolCalls(response.Text, response.ToolCalls));

            var executions = new List<AgentLoopToolExecution>(response.ToolCalls.Count);
            foreach (var call in response.ToolCalls.OrderBy(static value => value.Order))
            {
                var execution = await ExecuteToolAsync(
                    call,
                    context,
                    request.SessionId,
                    request.ProjectId,
                    cancellationToken);
                executions.Add(execution);
                messages.Add(ChatModelMessage.CreateToolResult(
                    call.Id,
                    call.Name,
                    SerializeToolResult(execution.Result)));
                await observer.RecordToolResultAsync(execution, iteration, cancellationToken);
            }

            await observer.CompleteToolTurnAsync(iteration, cancellationToken);
        }

        logger.LogWarning(
            "Agent loop reached the configured maximum of {MaxIterations} iterations.",
            options.MaxToolIterations);
        throw new AgentLoopException(
            AgentLoopErrorCode.MaxIterationsReached,
            $"The agent reached the maximum of {options.MaxToolIterations} tool iterations without producing a final response.");
    }

    private async Task<AgentLoopToolExecution> ExecuteToolAsync(
        ChatModelToolCall call,
        AgentToolExecutionContext context,
        SessionId? sessionId,
        ProjectId? projectId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        AgentToolResult result;

        if (!toolRegistry.TryGet(call.Name, out var tool) || tool is null)
        {
            result = AgentToolResult.Failure($"Unknown tool '{call.Name}'.");
        }
        else
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);
                var executionContext = context;
                if (permissionGate is not null)
                {
                    var authorizationContext = new PermissionAuthorizationContext();
                    var authorization = await permissionGate.AuthorizeAsync(
                        tool,
                        document.RootElement,
                        context,
                        sessionId,
                        projectId,
                        authorizationContext,
                        cancellationToken);
                    if (!authorization.IsAllowed)
                    {
                        result = authorization.Failure ?? AgentToolResult.Failure(
                            $"Permission denied for tool '{call.Name}'.");
                        stopwatch.Stop();
                        result = LimitOutput(result);
                        logger.LogInformation(
                            "Tool {ToolName} completed in {DurationMs} ms with success {Succeeded}.",
                            call.Name,
                            stopwatch.Elapsed.TotalMilliseconds,
                            result.Succeeded);
                        return new AgentLoopToolExecution(call, result, stopwatch.Elapsed);
                    }

                    executionContext = new AgentToolExecutionContext(
                        context.WorkspaceRoot,
                        new ResourcePermissionAuthorizer(
                            permissionGate,
                            tool,
                            context,
                            sessionId,
                            projectId,
                            authorizationContext));
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.ToolTimeout);
                result = await tool.ExecuteAsync(document.RootElement, executionContext, timeout.Token);
            }
            catch (JsonException exception)
            {
                result = AgentToolResult.Failure(
                    $"Invalid JSON arguments for tool '{call.Name}': {exception.Message}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                result = AgentToolResult.Failure(
                    $"Tool '{call.Name}' exceeded the timeout of {options.ToolTimeout}.");
            }
            catch (Exception exception)
            {
                result = AgentToolResult.Failure(
                    $"Tool '{call.Name}' failed: {exception.Message}");
            }
        }

        stopwatch.Stop();
        result = LimitOutput(result);
        logger.LogInformation(
            "Tool {ToolName} completed in {DurationMs} ms with success {Succeeded}.",
            call.Name,
            stopwatch.Elapsed.TotalMilliseconds,
            result.Succeeded);
        return new AgentLoopToolExecution(call, result, stopwatch.Elapsed);
    }

    private AgentToolResult LimitOutput(AgentToolResult result)
    {
        if (result.Output.Length <= options.MaxOutputCharacters)
        {
            return result;
        }

        var suffix = $"\n\n[Output truncated at {options.MaxOutputCharacters} characters.]";
        var available = Math.Max(0, options.MaxOutputCharacters - suffix.Length);
        var output = result.Output[..available] + suffix;
        var metadata = new Dictionary<string, string>(result.Metadata, StringComparer.Ordinal)
        {
            ["truncated"] = "true",
        };

        return result.Succeeded
            ? AgentToolResult.Success(output, metadata)
            : AgentToolResult.Failure(result.Error ?? "Tool failed.", output, metadata);
    }

    private static string SerializeToolResult(AgentToolResult result)
    {
        return JsonSerializer.Serialize(new
        {
            success = result.Succeeded,
            output = result.Output,
            error = result.Error,
            metadata = result.Metadata,
        });
    }

    private sealed class ResourcePermissionAuthorizer(
        IPermissionGate permissionGate,
        IAgentTool tool,
        AgentToolExecutionContext toolContext,
        SessionId? sessionId,
        ProjectId? projectId,
        PermissionAuthorizationContext authorizationContext)
        : IAgentToolResourcePermissionAuthorizer
    {
        public Task<PermissionAuthorizationResult> AuthorizeAsync(
            string operation,
            string target,
            string? description,
            CancellationToken cancellationToken)
        {
            return permissionGate.AuthorizeResourceAsync(
                tool,
                operation,
                target,
                description,
                toolContext,
                sessionId,
                projectId,
                authorizationContext,
                cancellationToken);
        }
    }
}
