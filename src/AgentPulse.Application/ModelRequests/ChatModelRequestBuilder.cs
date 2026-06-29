using AgentPulse.Application.ChatModels;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Domain.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPulse.Application.ModelRequests;

public sealed class ChatModelRequestBuilder : IChatModelRequestBuilder
{
    public const string TextPartSeparator = "\n";

    private readonly IChatModelHistoryPolicy _historyPolicy;
    private readonly ILogger<ChatModelRequestBuilder> _logger;

    public ChatModelRequestBuilder(
        IChatModelHistoryPolicy historyPolicy,
        ILogger<ChatModelRequestBuilder>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(historyPolicy);
        _historyPolicy = historyPolicy;
        _logger = logger ?? NullLogger<ChatModelRequestBuilder>.Instance;
    }

    public ChatModelRequest Build(ChatModelRequestBuildInput input)
    {
        if (input is null)
        {
            throw new ChatModelRequestException(
                ChatModelRequestErrorCode.InvalidInput,
                "Model request input is required.");
        }

        ValidateProjectContext(input.ProjectContext);

        if (input.OrderedPreviousHistory is null)
        {
            throw new ChatModelRequestException(
                ChatModelRequestErrorCode.InvalidInput,
                "Previous history is required.");
        }

        if (input.CurrentUserMessage is null)
        {
            throw new ChatModelRequestException(
                ChatModelRequestErrorCode.InvalidInput,
                "The current user message is required.");
        }

        var currentUserMessage = input.CurrentUserMessage;
        ValidateCurrentUserMessage(currentUserMessage);

        var history = input.OrderedPreviousHistory.ToArray();
        ValidateHistory(history, currentUserMessage);

        var requestMessages = new List<ChatModelMessage>(history.Length + 2)
        {
            new(
                ChatModelRole.System,
                ProjectSystemContextFormatter.Format(input.ProjectContext)),
        };

        var sendableHistory = history
            .Where(message =>
                message.Id != currentUserMessage.Id &&
                message.Sequence < currentUserMessage.Sequence &&
                _historyPolicy.ShouldInclude(message.Status))
            .OrderBy(static message => message.Sequence)
            .ToArray();
        var filteredToolTurnMessageIds = GetFilteredToolTurnMessageIds(sendableHistory);
        foreach (var message in sendableHistory)
        {
            if (filteredToolTurnMessageIds.Contains(message.Id))
            {
                continue;
            }

            requestMessages.Add(ConvertMessage(message, isCurrentUserMessage: false));
        }

        requestMessages.Add(ConvertMessage(currentUserMessage, isCurrentUserMessage: true));
        return new ChatModelRequest(requestMessages, input.Model);
    }

    private static void ValidateProjectContext(ProjectContext projectContext)
    {
        if (projectContext is null ||
            string.IsNullOrWhiteSpace(projectContext.CurrentDirectory) ||
            string.IsNullOrWhiteSpace(projectContext.ProjectRoot) ||
            projectContext.CurrentUtcDate.Kind != DateTimeKind.Utc ||
            !Enum.IsDefined(projectContext.Platform) ||
            projectContext.ProjectId.Value == Guid.Empty ||
            (projectContext.IsGitRepository && string.IsNullOrWhiteSpace(projectContext.GitWorktree)) ||
            (!projectContext.IsGitRepository && projectContext.GitWorktree is not null))
        {
            throw new ChatModelRequestException(
                ChatModelRequestErrorCode.InvalidProjectContext,
                "Project context is invalid.");
        }
    }

    private static void ValidateCurrentUserMessage(Message currentUserMessage)
    {
        if (!Enum.IsDefined(currentUserMessage.Role))
        {
            throw new ChatModelRequestException(
                ChatModelRequestErrorCode.UnsupportedMessageRole,
                $"Message role '{currentUserMessage.Role}' is not supported.");
        }

        if (currentUserMessage.Role != MessageRole.User)
        {
            throw new ChatModelRequestException(
                ChatModelRequestErrorCode.InvalidCurrentUserRole,
                "The current message must have the User role.");
        }

        if (currentUserMessage.Status != MessageStatus.Completed)
        {
            throw new ChatModelRequestException(
                ChatModelRequestErrorCode.InvalidCurrentUserStatus,
                "The current user message must be completed before building a model request.");
        }
    }

    private static void ValidateHistory(
        IReadOnlyCollection<Message> history,
        Message currentUserMessage)
    {
        if (history.Any(static message => message is null))
        {
            throw new ChatModelRequestException(
                ChatModelRequestErrorCode.InvalidInput,
                "Previous history cannot contain a null message.");
        }

        foreach (var message in history)
        {
            if (message.SessionId != currentUserMessage.SessionId)
            {
                throw new ChatModelRequestException(
                    ChatModelRequestErrorCode.SessionMismatch,
                    $"Message '{message.Id}' belongs to a different session.");
            }

            ValidateSupportedRole(message.Role);
        }

        var duplicateSequence = history
            .GroupBy(static message => message.Sequence)
            .FirstOrDefault(static group => group.Count() > 1);

        if (duplicateSequence is not null)
        {
            throw new ChatModelRequestException(
                ChatModelRequestErrorCode.DuplicateHistorySequence,
                $"Previous history contains duplicate sequence '{duplicateSequence.Key}'.");
        }

        var conflictingMessage = history.FirstOrDefault(message =>
            message.Id != currentUserMessage.Id &&
            message.Sequence == currentUserMessage.Sequence);

        if (conflictingMessage is not null)
        {
            throw new ChatModelRequestException(
                ChatModelRequestErrorCode.CurrentMessageSequenceConflict,
                $"Message '{conflictingMessage.Id}' conflicts with the current user message sequence.");
        }
    }

    private HashSet<MessageId> GetFilteredToolTurnMessageIds(
        IReadOnlyCollection<Message> sendableHistory)
    {
        var filtered = new HashSet<MessageId>();
        var includedToolMessageIds = new HashSet<MessageId>();
        var toolMessages = sendableHistory
            .Where(static message => message.Role == MessageRole.Tool)
            .ToArray();

        foreach (var assistant in sendableHistory.Where(static message =>
                     message.Role == MessageRole.Assistant))
        {
            var calls = assistant.Parts.OfType<ToolCallMessagePart>().ToArray();
            if (calls.Length == 0)
            {
                continue;
            }

            var invalidCallId = calls.Any(static call => string.IsNullOrWhiteSpace(call.ToolCallId));
            var duplicateCallId = calls.GroupBy(static call => call.ToolCallId, StringComparer.Ordinal)
                .Any(static group => group.Count() != 1);
            var associatedToolMessages = toolMessages
                .Select(static message => new
                {
                    Message = message,
                    OrderedParts = message.Parts.OrderBy(static part => part.Order).ToArray(),
                })
                .Where(value => value.OrderedParts
                    .OfType<ToolResultMessagePart>()
                    .Any(result => result.AssistantToolCallMessageId == assistant.Id &&
                                   result.SessionId == assistant.SessionId))
                .ToArray();
            var sendableResults = associatedToolMessages
                .Where(static value =>
                    value.OrderedParts.Length == 1 &&
                    value.OrderedParts[0] is ToolResultMessagePart)
                .Select(static value => new
                {
                    value.Message,
                    Result = (ToolResultMessagePart)value.OrderedParts[0],
                })
                .Where(value => calls.Any(call =>
                    string.Equals(value.Result.ToolCallId, call.ToolCallId, StringComparison.Ordinal) &&
                    string.Equals(value.Result.ToolName, call.ToolName, StringComparison.Ordinal)))
                .ToArray();

            var complete = !invalidCallId &&
                           !duplicateCallId &&
                           calls.All(call => sendableResults.Count(value =>
                               string.Equals(value.Result.ToolCallId, call.ToolCallId, StringComparison.Ordinal) &&
                               string.Equals(value.Result.ToolName, call.ToolName, StringComparison.Ordinal)) == 1);

            if (complete)
            {
                foreach (var result in sendableResults)
                {
                    includedToolMessageIds.Add(result.Message.Id);
                }

                continue;
            }

            filtered.Add(assistant.Id);

            _logger.LogInformation(
                "Filtered incomplete tool turn {AssistantMessageId} from session {SessionId} with {ToolCallCount} calls and {ToolResultCount} associated results.",
                assistant.Id,
                assistant.SessionId,
                calls.Length,
                sendableResults.Length);
        }

        foreach (var toolMessage in toolMessages)
        {
            if (!includedToolMessageIds.Contains(toolMessage.Id))
            {
                filtered.Add(toolMessage.Id);
            }
        }

        return filtered;
    }

    private static ChatModelMessage ConvertMessage(
        Message message,
        bool isCurrentUserMessage)
    {
        return message.Role switch
        {
            MessageRole.User => new ChatModelMessage(
                ChatModelRole.User,
                CombineTextParts(message, isCurrentUserMessage, allowEmpty: false)),
            MessageRole.Assistant => ConvertAssistantMessage(message),
            MessageRole.Tool => ConvertToolMessage(message),
            _ => throw new ChatModelRequestException(
                ChatModelRequestErrorCode.UnsupportedMessageRole,
                $"Message role '{message.Role}' is not supported."),
        };
    }

    private static ChatModelMessage ConvertAssistantMessage(Message message)
    {
        var orderedParts = message.Parts.OrderBy(static part => part.Order).ToArray();
        var calls = orderedParts
            .OfType<ToolCallMessagePart>()
            .Select(static part => new ChatModelToolCall(
                part.ToolCallId,
                part.ToolName,
                part.ArgumentsJson,
                part.Order))
            .ToArray();
        var content = CombineTextParts(message, isCurrentUserMessage: false, allowEmpty: calls.Length > 0);

        if (calls.Length > 0)
        {
            return ChatModelMessage.CreateAssistantToolCalls(
                string.IsNullOrEmpty(content) ? null : content,
                calls);
        }

        return new ChatModelMessage(ChatModelRole.Assistant, content);
    }

    private static ChatModelMessage ConvertToolMessage(Message message)
    {
        var orderedParts = message.Parts.OrderBy(static part => part.Order).ToArray();
        if (orderedParts.Length != 1 || orderedParts[0] is not ToolResultMessagePart result)
        {
            throw new ChatModelRequestException(
                ChatModelRequestErrorCode.UnsupportedMessagePart,
                $"Tool message '{message.Id}' must contain exactly one tool result part.");
        }

        var content = System.Text.Json.JsonSerializer.Serialize(new
        {
            success = result.Succeeded,
            output = result.Output,
            error = result.Error,
            metadata = ParseToolMetadata(result.MetadataJson),
        });
        return ChatModelMessage.CreateToolResult(result.ToolCallId, result.ToolName, content);
    }

    private static System.Text.Json.JsonElement? ParseToolMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(metadataJson);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new ChatModelRequestException(
                ChatModelRequestErrorCode.UnsupportedMessagePart,
                "A stored tool result contains invalid metadata JSON.",
                exception);
        }
    }

    private static ChatModelRole ConvertRole(MessageRole role)
    {
        return role switch
        {
            MessageRole.User => ChatModelRole.User,
            MessageRole.Assistant => ChatModelRole.Assistant,
            MessageRole.Tool => ChatModelRole.Tool,
            _ => throw new ChatModelRequestException(
                ChatModelRequestErrorCode.UnsupportedMessageRole,
                $"Message role '{role}' is not supported."),
        };
    }

    private static void ValidateSupportedRole(MessageRole role)
    {
        _ = ConvertRole(role);
    }

    private static string CombineTextParts(
        Message message,
        bool isCurrentUserMessage,
        bool allowEmpty)
    {
        var orderedParts = message.Parts
            .OrderBy(static part => part.Order)
            .ToArray();
        var textParts = orderedParts
            .OfType<TextMessagePart>()
            .Select(static part => part.Text)
            .ToArray();

        var unsupported = orderedParts.FirstOrDefault(static part =>
            part is not TextMessagePart and not ToolCallMessagePart);
        if (unsupported is not null)
        {
            throw new ChatModelRequestException(
                ChatModelRequestErrorCode.UnsupportedMessagePart,
                $"Message part type '{unsupported.GetType().Name}' is not supported for role '{message.Role}'.");
        }

        var content = string.Join(TextPartSeparator, textParts);
        if (!allowEmpty && string.IsNullOrWhiteSpace(content))
        {
            var errorCode = isCurrentUserMessage
                ? ChatModelRequestErrorCode.EmptyUserPrompt
                : ChatModelRequestErrorCode.MessageHasNoUsableText;
            var description = isCurrentUserMessage
                ? "The current user prompt cannot be empty."
                : $"Message '{message.Id}' has no usable text content.";
            throw new ChatModelRequestException(errorCode, description);
        }

        return content;
    }

}
