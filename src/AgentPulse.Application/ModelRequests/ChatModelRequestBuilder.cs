using AgentPulse.Application.ChatModels;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Domain.Messages;

namespace AgentPulse.Application.ModelRequests;

public sealed class ChatModelRequestBuilder : IChatModelRequestBuilder
{
    public const string TextPartSeparator = "\n";

    private readonly IChatModelHistoryPolicy _historyPolicy;

    public ChatModelRequestBuilder(IChatModelHistoryPolicy historyPolicy)
    {
        ArgumentNullException.ThrowIfNull(historyPolicy);
        _historyPolicy = historyPolicy;
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

        foreach (var message in history.OrderBy(static message => message.Sequence))
        {
            if (message.Id == currentUserMessage.Id ||
                message.Sequence >= currentUserMessage.Sequence)
            {
                continue;
            }

            if (!_historyPolicy.ShouldInclude(message.Status))
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

    private static ChatModelMessage ConvertMessage(
        Message message,
        bool isCurrentUserMessage)
    {
        var role = ConvertRole(message.Role);
        var content = CombineTextParts(message, isCurrentUserMessage);
        return new ChatModelMessage(role, content);
    }

    private static ChatModelRole ConvertRole(MessageRole role)
    {
        return role switch
        {
            MessageRole.User => ChatModelRole.User,
            MessageRole.Assistant => ChatModelRole.Assistant,
            _ => throw new ChatModelRequestException(
                ChatModelRequestErrorCode.UnsupportedMessageRole,
                $"Message role '{role}' is not supported."),
        };
    }

    private static void ValidateSupportedRole(MessageRole role)
    {
        _ = ConvertRole(role);
    }

    private static string CombineTextParts(Message message, bool isCurrentUserMessage)
    {
        var orderedParts = message.Parts
            .OrderBy(static part => part.Order)
            .ToArray();

        var textParts = new string[orderedParts.Length];

        for (var index = 0; index < orderedParts.Length; index++)
        {
            if (orderedParts[index] is not TextMessagePart textPart)
            {
                throw new ChatModelRequestException(
                    ChatModelRequestErrorCode.UnsupportedMessagePart,
                    $"Message part type '{orderedParts[index].GetType().Name}' is not supported.");
            }

            textParts[index] = textPart.Text;
        }

        var content = string.Join(TextPartSeparator, textParts);

        if (string.IsNullOrWhiteSpace(content))
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
