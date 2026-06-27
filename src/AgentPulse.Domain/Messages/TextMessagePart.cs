namespace AgentPulse.Domain.Messages;

public sealed class TextMessagePart : MessagePart
{
    private TextMessagePart()
    {
        Text = null!;
    }

    internal TextMessagePart(
        MessagePartId id,
        MessageId messageId,
        int order,
        string text,
        DateTime createdAtUtc)
        : base(id, messageId, order, createdAtUtc)
    {
        Text = RequireText(text);
    }

    public string Text { get; private set; }

    public void ReplaceText(string text, DateTime updatedAtUtc)
    {
        var validatedText = RequireText(text);
        var validatedUpdatedAtUtc = EnsureUpdateTimestamp(updatedAtUtc);

        Text = validatedText;
        UpdatedAtUtc = validatedUpdatedAtUtc;
    }

    private static string RequireText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length > 0 && string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException(
                "Text must either be empty or contain a non-whitespace character.",
                nameof(text));
        }

        return text;
    }
}
