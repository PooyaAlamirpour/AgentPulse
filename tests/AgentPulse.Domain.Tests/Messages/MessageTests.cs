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

}
