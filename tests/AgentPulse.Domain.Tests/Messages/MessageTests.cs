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
    public void Text_part_requires_text_and_preserves_utc_updates()
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
        Assert.Throws<ArgumentException>(() => part.ReplaceText(" ", createdAtUtc.AddSeconds(2)));
        Assert.Equal("hello world", part.Text);
    }
}
