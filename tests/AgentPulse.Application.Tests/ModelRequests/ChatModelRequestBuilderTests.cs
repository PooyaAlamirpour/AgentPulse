using System.Reflection;
using AgentPulse.Application.ChatModels;
using AgentPulse.Application.ModelRequests;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Domain.Messages;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.Tests.ModelRequests;

public sealed class ChatModelRequestBuilderTests
{
    private static readonly DateTime UtcDate =
        new(2026, 6, 27, 0, 0, 0, DateTimeKind.Utc);

    private static readonly SessionId SessionId =
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static readonly ProjectId ProjectId =
        new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private readonly IChatModelRequestBuilder _builder =
        new ChatModelRequestBuilder(new ChatModelHistoryPolicy());

    [Fact]
    public void Request_has_system_first_history_in_sequence_order_and_current_user_last()
    {
        var historyAssistant = CreateMessage(
            SessionId,
            MessageRole.Assistant,
            2,
            MessageStatus.Completed,
            "second");
        var historyUser = CreateMessage(
            SessionId,
            MessageRole.User,
            1,
            MessageStatus.Completed,
            "first");
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            3,
            MessageStatus.Completed,
            "current");

        var request = Build([historyAssistant, historyUser], currentUser);

        Assert.Equal(
            [
                ChatModelRole.System,
                ChatModelRole.User,
                ChatModelRole.Assistant,
                ChatModelRole.User,
            ],
            request.Messages.Select(static message => message.Role));
        Assert.Equal(
            ["first", "second", "current"],
            request.Messages.Skip(1).Select(static message => message.Content));
    }

    [Fact]
    public void Empty_history_produces_only_system_and_current_user()
    {
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            1,
            MessageStatus.Completed,
            "current");

        var request = Build([], currentUser);

        Assert.Equal(2, request.Messages.Count);
        Assert.Equal(ChatModelRole.System, request.Messages[0].Role);
        Assert.Equal(ChatModelRole.User, request.Messages[^1].Role);
        Assert.Equal("current", request.Messages[^1].Content);
    }

    [Fact]
    public void System_context_contains_every_project_field_with_stable_iso_utc_format()
    {
        var context = new ProjectContext(
            "/repo/src",
            "/repo",
            true,
            "/repo",
            ProjectPlatform.Linux,
            UtcDate,
            ProjectId);
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            1,
            MessageStatus.Completed,
            "prompt");

        var request = _builder.Build(new ChatModelRequestBuildInput(context, [], currentUser));

        Assert.Equal(
            "You are operating in the following project context:\n" +
            "\n" +
            "Project ID: 22222222-2222-2222-2222-222222222222\n" +
            "Current directory: /repo/src\n" +
            "Project root: /repo\n" +
            "Git repository: true\n" +
            "Git worktree: /repo\n" +
            "Platform: Linux\n" +
            "Current UTC date: 2026-06-27T00:00:00.0000000Z",
            request.Messages[0].Content);
    }

    [Theory]
    [MemberData(nameof(ProjectContextCases))]
    public void System_context_is_stable_for_regular_repository_worktree_and_non_git_directory(
        ProjectContext context,
        string expectedGitRepository,
        string expectedWorktree)
    {
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            1,
            MessageStatus.Completed,
            "prompt");

        var system = _builder
            .Build(new ChatModelRequestBuildInput(context, [], currentUser))
            .Messages[0]
            .Content;

        Assert.Contains($"Git repository: {expectedGitRepository}", system, StringComparison.Ordinal);
        Assert.Contains($"Git worktree: {expectedWorktree}", system, StringComparison.Ordinal);
        Assert.Contains($"Project root: {context.ProjectRoot}", system, StringComparison.Ordinal);
    }

    public static TheoryData<ProjectContext, string, string> ProjectContextCases => new()
    {
        {
            new ProjectContext(
                "/repo/src",
                "/repo",
                true,
                "/repo",
                ProjectPlatform.Linux,
                UtcDate,
                ProjectId),
            "true",
            "/repo"
        },
        {
            new ProjectContext(
                "/worktrees/feature/src",
                "/worktrees/feature",
                true,
                "/worktrees/feature",
                ProjectPlatform.Windows,
                UtcDate,
                ProjectId),
            "true",
            "/worktrees/feature"
        },
        {
            new ProjectContext(
                "/folder",
                "/folder",
                false,
                null,
                ProjectPlatform.MacOs,
                UtcDate,
                ProjectId),
            "false",
            "(none)"
        },
    };

    [Fact]
    public void Multiple_text_parts_are_sorted_by_part_order_and_joined_with_fixed_separator()
    {
        var historyMessage = CreateMessage(
            SessionId,
            MessageRole.User,
            1,
            MessageStatus.Completed,
            "first",
            "second",
            "third");
        ReverseParts(historyMessage);
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            2,
            MessageStatus.Completed,
            "current");

        var request = Build([historyMessage], currentUser);

        Assert.Equal("first\nsecond\nthird", request.Messages[1].Content);
        Assert.Equal("\n", ChatModelRequestBuilder.TextPartSeparator);
    }

    [Fact]
    public void User_and_assistant_roles_are_converted_without_provider_types()
    {
        var history = new[]
        {
            CreateMessage(SessionId, MessageRole.User, 1, MessageStatus.Completed, "user"),
            CreateMessage(SessionId, MessageRole.Assistant, 2, MessageStatus.Completed, "assistant"),
        };
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            3,
            MessageStatus.Completed,
            "current");

        var request = Build(history, currentUser);

        Assert.Equal(ChatModelRole.User, request.Messages[1].Role);
        Assert.Equal(ChatModelRole.Assistant, request.Messages[2].Role);
    }

    [Fact]
    public void Completed_history_is_included()
    {
        var completed = CreateMessage(
            SessionId,
            MessageRole.Assistant,
            1,
            MessageStatus.Completed,
            "completed");
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            2,
            MessageStatus.Completed,
            "current");

        var request = Build([completed], currentUser);

        Assert.Contains(request.Messages, static message => message.Content == "completed");
    }

    [Theory]
    [InlineData(MessageStatus.Pending)]
    [InlineData(MessageStatus.Streaming)]
    [InlineData(MessageStatus.Failed)]
    [InlineData(MessageStatus.Cancelled)]
    public void Incomplete_history_is_excluded_by_central_policy(MessageStatus status)
    {
        var incomplete = CreateMessage(
            SessionId,
            MessageRole.Assistant,
            1,
            status,
            "partial text remains in the domain message");
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            2,
            MessageStatus.Completed,
            "current");

        var request = Build([incomplete], currentUser);

        Assert.Equal(2, request.Messages.Count);
        Assert.Equal("partial text remains in the domain message", GetTextPart(incomplete).Text);
    }

    [Fact]
    public void Current_user_is_added_once_by_identifier_even_when_present_in_history()
    {
        var oldUser = CreateMessage(
            SessionId,
            MessageRole.User,
            1,
            MessageStatus.Completed,
            "old");
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            2,
            MessageStatus.Completed,
            "same prompt");

        var request = Build([currentUser, oldUser], currentUser);

        Assert.Equal(1, request.Messages.Count(message => message.Content == "same prompt"));
        Assert.Equal("same prompt", request.Messages[^1].Content);
    }

    [Fact]
    public void Current_run_assistant_is_excluded_by_sequence_boundary()
    {
        var oldAssistant = CreateMessage(
            SessionId,
            MessageRole.Assistant,
            1,
            MessageStatus.Completed,
            "old answer");
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            2,
            MessageStatus.Completed,
            "current");
        var currentAssistant = CreateMessage(
            SessionId,
            MessageRole.Assistant,
            3,
            MessageStatus.Streaming,
            string.Empty);

        var request = Build([currentAssistant, currentUser, oldAssistant], currentUser);

        Assert.Equal(["old answer", "current"], request.Messages.Skip(1).Select(static message => message.Content));
    }

    [Fact]
    public void Old_messages_with_identical_text_are_not_deduplicated_by_content()
    {
        var first = CreateMessage(
            SessionId,
            MessageRole.User,
            1,
            MessageStatus.Completed,
            "repeated");
        var second = CreateMessage(
            SessionId,
            MessageRole.User,
            2,
            MessageStatus.Completed,
            "repeated");
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            3,
            MessageStatus.Completed,
            "current");

        var request = Build([second, first], currentUser);

        Assert.Equal(2, request.Messages.Count(message => message.Content == "repeated"));
    }

    [Fact]
    public void Message_from_another_session_is_rejected()
    {
        var otherSession = new SessionId(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var foreignMessage = CreateMessage(
            otherSession,
            MessageRole.User,
            1,
            MessageStatus.Completed,
            "foreign");
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            2,
            MessageStatus.Completed,
            "current");

        var exception = Assert.Throws<ChatModelRequestException>(() =>
            Build([foreignMessage], currentUser));

        Assert.Equal(ChatModelRequestErrorCode.SessionMismatch, exception.ErrorCode);
    }

    [Fact]
    public void Duplicate_history_sequence_is_rejected()
    {
        var first = CreateMessage(
            SessionId,
            MessageRole.User,
            1,
            MessageStatus.Completed,
            "first");
        var second = CreateMessage(
            SessionId,
            MessageRole.Assistant,
            1,
            MessageStatus.Completed,
            "second");
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            2,
            MessageStatus.Completed,
            "current");

        var exception = Assert.Throws<ChatModelRequestException>(() =>
            Build([first, second], currentUser));

        Assert.Equal(ChatModelRequestErrorCode.DuplicateHistorySequence, exception.ErrorCode);
    }

    [Fact]
    public void Unknown_history_role_is_rejected()
    {
        var historyMessage = CreateMessage(
            SessionId,
            MessageRole.User,
            1,
            MessageStatus.Completed,
            "history");
        SetRole(historyMessage, (MessageRole)999);
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            2,
            MessageStatus.Completed,
            "current");

        var exception = Assert.Throws<ChatModelRequestException>(() =>
            Build([historyMessage], currentUser));

        Assert.Equal(ChatModelRequestErrorCode.UnsupportedMessageRole, exception.ErrorCode);
    }

    [Fact]
    public void Unsupported_part_is_rejected_instead_of_silently_ignored()
    {
        var historyMessage = CreateMessage(
            SessionId,
            MessageRole.Assistant,
            1,
            MessageStatus.Completed,
            "text");
        AddUnsupportedPart(historyMessage);
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            2,
            MessageStatus.Completed,
            "current");

        var exception = Assert.Throws<ChatModelRequestException>(() =>
            Build([historyMessage], currentUser));

        Assert.Equal(ChatModelRequestErrorCode.UnsupportedMessagePart, exception.ErrorCode);
    }

    [Fact]
    public void Empty_current_user_prompt_is_rejected()
    {
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            1,
            MessageStatus.Completed,
            string.Empty);

        var exception = Assert.Throws<ChatModelRequestException>(() =>
            Build([], currentUser));

        Assert.Equal(ChatModelRequestErrorCode.EmptyUserPrompt, exception.ErrorCode);
    }

    [Fact]
    public void Completed_history_without_usable_text_is_rejected()
    {
        var emptyHistory = CreateMessage(
            SessionId,
            MessageRole.Assistant,
            1,
            MessageStatus.Completed,
            string.Empty);
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            2,
            MessageStatus.Completed,
            "current");

        var exception = Assert.Throws<ChatModelRequestException>(() =>
            Build([emptyHistory], currentUser));

        Assert.Equal(ChatModelRequestErrorCode.MessageHasNoUsableText, exception.ErrorCode);
    }

    [Fact]
    public void Current_message_with_non_user_role_is_rejected()
    {
        var currentAssistant = CreateMessage(
            SessionId,
            MessageRole.Assistant,
            1,
            MessageStatus.Completed,
            "not user");

        var exception = Assert.Throws<ChatModelRequestException>(() =>
            Build([], currentAssistant));

        Assert.Equal(ChatModelRequestErrorCode.InvalidCurrentUserRole, exception.ErrorCode);
    }

    [Theory]
    [InlineData(MessageStatus.Pending)]
    [InlineData(MessageStatus.Streaming)]
    [InlineData(MessageStatus.Failed)]
    [InlineData(MessageStatus.Cancelled)]
    public void Current_user_with_non_usable_status_is_rejected(MessageStatus status)
    {
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            1,
            status,
            "prompt");

        var exception = Assert.Throws<ChatModelRequestException>(() =>
            Build([], currentUser));

        Assert.Equal(ChatModelRequestErrorCode.InvalidCurrentUserStatus, exception.ErrorCode);
    }

    [Fact]
    public void Invalid_project_context_is_rejected_with_application_error()
    {
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            1,
            MessageStatus.Completed,
            "prompt");

        var exception = Assert.Throws<ChatModelRequestException>(() =>
            _builder.Build(new ChatModelRequestBuildInput(null!, [], currentUser)));

        Assert.Equal(ChatModelRequestErrorCode.InvalidProjectContext, exception.ErrorCode);
    }

    [Fact]
    public void Built_request_and_messages_are_immutable_snapshots()
    {
        var history = new List<Message>
        {
            CreateMessage(SessionId, MessageRole.User, 1, MessageStatus.Completed, "old"),
        };
        var currentUser = CreateMessage(
            SessionId,
            MessageRole.User,
            2,
            MessageStatus.Completed,
            "current");

        var request = Build(history, currentUser);
        history.Clear();
        GetTextPart(currentUser).ReplaceText("changed later", UtcDate.AddMinutes(1));

        Assert.Equal(["old", "current"], request.Messages.Skip(1).Select(static message => message.Content));
        var mutableView = Assert.IsAssignableFrom<IList<ChatModelMessage>>(request.Messages);
        Assert.Throws<NotSupportedException>(() =>
            mutableView.RemoveAt(0));
    }

    private ChatModelRequest Build(
        IReadOnlyCollection<Message> history,
        Message currentUser,
        ProjectContext? context = null)
    {
        return _builder.Build(new ChatModelRequestBuildInput(
            context ?? CreateProjectContext(),
            history,
            currentUser));
    }

    private static ProjectContext CreateProjectContext()
    {
        return new ProjectContext(
            "/workspace/project",
            "/workspace/project",
            false,
            null,
            ProjectPlatform.Linux,
            UtcDate,
            ProjectId);
    }

    private static Message CreateMessage(
        SessionId sessionId,
        MessageRole role,
        long sequence,
        MessageStatus status,
        params string[] textParts)
    {
        var message = new Message(
            MessageId.New(),
            sessionId,
            role,
            sequence,
            UtcDate);

        for (var index = 0; index < textParts.Length; index++)
        {
            message.AddTextPart(
                MessagePartId.New(),
                index + 1,
                textParts[index],
                UtcDate);
        }

        switch (status)
        {
            case MessageStatus.Pending:
                break;
            case MessageStatus.Streaming:
                message.StartStreaming(UtcDate);
                break;
            case MessageStatus.Completed:
                message.Complete(UtcDate);
                break;
            case MessageStatus.Failed:
                message.Fail("test failure", UtcDate);
                break;
            case MessageStatus.Cancelled:
                message.Cancel(UtcDate);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, null);
        }

        return message;
    }

    private static TextMessagePart GetTextPart(Message message)
    {
        return Assert.IsType<TextMessagePart>(message.Parts.Single());
    }

    private static void ReverseParts(Message message)
    {
        var parts = GetMutableParts(message);
        parts.Reverse();
    }

    private static void AddUnsupportedPart(Message message)
    {
        var parts = GetMutableParts(message);
        parts.Add(new UnsupportedMessagePart(
            MessagePartId.New(),
            message.Id,
            parts.Count + 1,
            UtcDate));
    }

    private static List<MessagePart> GetMutableParts(Message message)
    {
        var partsField = typeof(Message).GetField(
            "_parts",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Message parts field was not found.");

        return Assert.IsType<List<MessagePart>>(partsField.GetValue(message));
    }

    private static void SetRole(Message message, MessageRole role)
    {
        var roleField = typeof(Message).GetField(
            "<Role>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Message role field was not found.");

        roleField.SetValue(message, role);
    }

    private sealed class UnsupportedMessagePart : MessagePart
    {
        public UnsupportedMessagePart(
            MessagePartId id,
            MessageId messageId,
            int order,
            DateTime createdAtUtc)
            : base(id, messageId, order, createdAtUtc)
        {
        }
    }
}
