using AgentPulse.Domain.Messages;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Domain.Tests.Messages;

public sealed class MessageTests
{
    [Fact]
    public void Message_requires_positive_sequence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Message(
            MessageId.New(),
            SessionId.New(),
            MessageRole.User,
            0,
            DateTime.UtcNow));
    }

    [Fact]
    public void Message_allows_only_valid_lifecycle_transitions()
    {
        var createdAtUtc = new DateTime(2026, 6, 27, 10, 0, 0, DateTimeKind.Utc);
        var message = new Message(
            MessageId.New(),
            SessionId.New(),
            MessageRole.Assistant,
            1,
            createdAtUtc);

        message.StartStreaming(createdAtUtc.AddSeconds(1));
        message.Complete(createdAtUtc.AddSeconds(2));

        Assert.Equal(MessageStatus.Completed, message.Status);
        Assert.Throws<InvalidOperationException>(() => message.Fail(createdAtUtc.AddSeconds(3)));
        Assert.Throws<InvalidOperationException>(() => message.Cancel(createdAtUtc.AddSeconds(3)));
    }

    [Fact]
    public void Message_rejects_duplicate_part_order()
    {
        var createdAtUtc = new DateTime(2026, 6, 27, 10, 0, 0, DateTimeKind.Utc);
        var message = new Message(
            MessageId.New(),
            SessionId.New(),
            MessageRole.User,
            1,
            createdAtUtc);

        message.AddTextPart(MessagePartId.New(), 1, "first", createdAtUtc);

        Assert.Throws<InvalidOperationException>(() => message.AddTextPart(
            MessagePartId.New(),
            1,
            "duplicate order",
            createdAtUtc));
    }

    [Fact]
    public void Text_part_allows_empty_streaming_buffer_and_preserves_utc_updates()
    {
        var createdAtUtc = new DateTime(2026, 6, 27, 10, 0, 0, DateTimeKind.Utc);
        var message = new Message(
            MessageId.New(),
            SessionId.New(),
            MessageRole.User,
            1,
            createdAtUtc);
        var part = message.AddTextPart(MessagePartId.New(), 1, "hello", createdAtUtc);

        part.ReplaceText("hello world", createdAtUtc.AddSeconds(1));

        Assert.Equal("hello world", part.Text);
        Assert.Equal(DateTimeKind.Utc, part.UpdatedAtUtc.Kind);
        part.ReplaceText(string.Empty, createdAtUtc.AddSeconds(2));
        Assert.Equal(string.Empty, part.Text);
        Assert.Throws<ArgumentException>(() => part.ReplaceText(" ", createdAtUtc.AddSeconds(3)));
        Assert.Throws<ArgumentNullException>(() => part.ReplaceText(null!, createdAtUtc.AddSeconds(4)));
    }
    [Fact]
    public void Assistant_completion_records_model_finish_reason_and_usage()
    {
        var createdAtUtc = new DateTime(2026, 6, 27, 10, 0, 0, DateTimeKind.Utc);
        var message = new Message(
            MessageId.New(),
            SessionId.New(),
            MessageRole.Assistant,
            1,
            createdAtUtc);

        message.StartStreaming("custom-model", createdAtUtc.AddSeconds(1));
        message.Complete("Stop", 4, 2, 6, createdAtUtc.AddSeconds(2));

        Assert.Equal(MessageStatus.Completed, message.Status);
        Assert.Equal("custom-model", message.Model);
        Assert.Equal("Stop", message.FinishReason);
        Assert.Equal(4, message.InputTokens);
        Assert.Equal(2, message.OutputTokens);
        Assert.Equal(6, message.TotalTokens);
        Assert.Null(message.FailureReason);
        Assert.Null(message.FailureKind);
    }

    [Fact]
    public void Assistant_cancellation_and_failure_preserve_safe_terminal_metadata()
    {
        var createdAtUtc = new DateTime(2026, 6, 27, 10, 0, 0, DateTimeKind.Utc);
        var cancelled = new Message(
            MessageId.New(),
            SessionId.New(),
            MessageRole.Assistant,
            1,
            createdAtUtc);
        cancelled.StartStreaming("model-a", createdAtUtc.AddSeconds(1));
        cancelled.Cancel("Cancelled", createdAtUtc.AddSeconds(2));

        var failed = new Message(
            MessageId.New(),
            SessionId.New(),
            MessageRole.Assistant,
            2,
            createdAtUtc);
        failed.StartStreaming("model-b", createdAtUtc.AddSeconds(1));
        failed.Fail(
            "Provider unavailable.",
            "Unavailable",
            "AfterFirstToken",
            503,
            createdAtUtc.AddSeconds(2));

        Assert.Equal(MessageStatus.Cancelled, cancelled.Status);
        Assert.Equal("model-a", cancelled.Model);
        Assert.Equal("Cancelled", cancelled.FinishReason);
        Assert.Equal(MessageStatus.Failed, failed.Status);
        Assert.Equal("model-b", failed.Model);
        Assert.Equal("Provider unavailable.", failed.FailureReason);
        Assert.Equal("Unavailable", failed.FailureKind);
        Assert.Equal("AfterFirstToken", failed.FailureStage);
        Assert.Equal(503, failed.FailureStatusCode);
        Assert.Null(failed.FinishReason);
    }

    [Fact]
    public void Assistant_message_can_record_ordered_tool_calls()
    {
        var createdAtUtc = new DateTime(2026, 6, 28, 10, 0, 0, DateTimeKind.Utc);
        var message = new Message(
            MessageId.New(),
            SessionId.New(),
            MessageRole.Assistant,
            1,
            createdAtUtc);
        message.AddTextPart(MessagePartId.New(), 1, string.Empty, createdAtUtc);

        var part = message.AddToolCallPart(
            MessagePartId.New(),
            2,
            "call-1",
            "read",
            "{\"path\":\"README.md\"}",
            createdAtUtc);

        Assert.Equal("call-1", part.ToolCallId);
        Assert.Equal("read", part.ToolName);
        Assert.Equal("{\"path\":\"README.md\"}", part.ArgumentsJson);
        Assert.Equal(2, part.Order);
    }

    [Fact]
    public void Tool_message_can_record_a_linked_success_or_failure_result()
    {
        var createdAtUtc = new DateTime(2026, 6, 28, 10, 0, 0, DateTimeKind.Utc);
        var successSessionId = SessionId.New();
        var successAssistantId = MessageId.New();
        var success = new Message(
            MessageId.New(),
            successSessionId,
            MessageRole.Tool,
            1,
            createdAtUtc);
        var successPart = success.AddToolResultPart(
            MessagePartId.New(),
            successSessionId,
            successAssistantId,
            1,
            "call-1",
            "read",
            true,
            "content",
            null,
            "{\"truncated\":false}",
            createdAtUtc);
        success.Complete(createdAtUtc);

        var failureSessionId = SessionId.New();
        var failureAssistantId = MessageId.New();
        var failure = new Message(
            MessageId.New(),
            failureSessionId,
            MessageRole.Tool,
            2,
            createdAtUtc);
        var failurePart = failure.AddToolResultPart(
            MessagePartId.New(),
            failureSessionId,
            failureAssistantId,
            1,
            "call-2",
            "grep",
            false,
            string.Empty,
            "invalid pattern",
            null,
            createdAtUtc);

        Assert.True(successPart.Succeeded);
        Assert.Equal("call-1", successPart.ToolCallId);
        Assert.Equal(MessageStatus.Completed, success.Status);
        Assert.False(failurePart.Succeeded);
        Assert.Equal("invalid pattern", failurePart.Error);
    }

    [Fact]
    public void Tool_call_and_result_parts_are_restricted_to_their_roles()
    {
        var createdAtUtc = new DateTime(2026, 6, 28, 10, 0, 0, DateTimeKind.Utc);
        var user = new Message(
            MessageId.New(),
            SessionId.New(),
            MessageRole.User,
            1,
            createdAtUtc);
        var assistant = new Message(
            MessageId.New(),
            SessionId.New(),
            MessageRole.Assistant,
            2,
            createdAtUtc);

        Assert.Throws<InvalidOperationException>(() => user.AddToolCallPart(
            MessagePartId.New(), 1, "call", "read", "{}", createdAtUtc));
        Assert.Throws<InvalidOperationException>(() => assistant.AddToolResultPart(
            MessagePartId.New(), assistant.SessionId, assistant.Id, 1, "call", "read", true, "ok", null, null, createdAtUtc));
    }

}
