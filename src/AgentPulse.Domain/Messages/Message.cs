using AgentPulse.Domain.Common;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Domain.Messages;

public sealed class Message
{
    private const int MaximumModelLength = 256;
    private const int MaximumFinishReasonLength = 64;
    private const int MaximumFailureReasonLength = 1024;
    private const int MaximumFailureMetadataLength = 64;
    private readonly List<MessagePart> _parts = [];

    private Message()
    {
    }

    public Message(
        MessageId id,
        SessionId sessionId,
        MessageRole role,
        long sequence,
        DateTime createdAtUtc)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("Message identifier cannot be empty.", nameof(id));
        }

        if (sessionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Session identifier cannot be empty.", nameof(sessionId));
        }

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown message role.");
        }

        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequence),
                sequence,
                "Message sequence must be greater than zero.");
        }

        Id = id;
        SessionId = sessionId;
        Role = role;
        Sequence = sequence;
        Status = MessageStatus.Pending;
        CreatedAtUtc = UtcDateTime.Ensure(createdAtUtc, nameof(createdAtUtc));
        UpdatedAtUtc = CreatedAtUtc;
    }

    public MessageId Id { get; private set; }

    public SessionId SessionId { get; private set; }

    public MessageRole Role { get; private set; }

    public MessageStatus Status { get; private set; }

    public long Sequence { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public string? FailureReason { get; private set; }

    public string? Model { get; private set; }

    public string? FinishReason { get; private set; }

    public long? InputTokens { get; private set; }

    public long? OutputTokens { get; private set; }

    public long? TotalTokens { get; private set; }

    public string? FailureKind { get; private set; }

    public string? FailureStage { get; private set; }

    public int? FailureStatusCode { get; private set; }

    public IReadOnlyCollection<MessagePart> Parts => _parts.AsReadOnly();

    public TextMessagePart AddTextPart(
        MessagePartId id,
        int order,
        string text,
        DateTime createdAtUtc)
    {
        EnsurePartCanBeAdded(id, order);
        var part = new TextMessagePart(id, Id, order, text, createdAtUtc);
        _parts.Add(part);
        return part;
    }

    public ToolCallMessagePart AddToolCallPart(
        MessagePartId id,
        int order,
        string toolCallId,
        string toolName,
        string argumentsJson,
        DateTime createdAtUtc)
    {
        EnsureAssistant();
        EnsurePartCanBeAdded(id, order);
        var part = new ToolCallMessagePart(
            id,
            Id,
            order,
            toolCallId,
            toolName,
            argumentsJson,
            createdAtUtc);
        _parts.Add(part);
        return part;
    }

    public ToolResultMessagePart AddToolResultPart(
        MessagePartId id,
        SessionId sessionId,
        MessageId assistantToolCallMessageId,
        int order,
        string toolCallId,
        string toolName,
        bool succeeded,
        string output,
        string? error,
        string? metadataJson,
        DateTime createdAtUtc)
    {
        if (Role != MessageRole.Tool)
        {
            throw new InvalidOperationException(
                "Only tool messages can contain tool result parts.");
        }

        EnsurePartCanBeAdded(id, order);
        var part = new ToolResultMessagePart(
            id,
            Id,
            sessionId,
            assistantToolCallMessageId,
            order,
            toolCallId,
            toolName,
            succeeded,
            output,
            error,
            metadataJson,
            createdAtUtc);
        _parts.Add(part);
        return part;
    }

    public void StartStreaming(DateTime updatedAtUtc)
    {
        TransitionTo(MessageStatus.Streaming, updatedAtUtc, MessageStatus.Pending);
    }

    public void StartStreaming(string model, DateTime updatedAtUtc)
    {
        EnsureAssistant();
        Model = NormalizeRequired(model, MaximumModelLength, nameof(model));
        StartStreaming(updatedAtUtc);
    }

    public void Complete(DateTime updatedAtUtc)
    {
        Complete(
            finishReason: null,
            inputTokens: null,
            outputTokens: null,
            totalTokens: null,
            updatedAtUtc: updatedAtUtc);
    }

    public void Complete(
        string? finishReason,
        long? inputTokens,
        long? outputTokens,
        long? totalTokens,
        DateTime updatedAtUtc)
    {
        if (finishReason is not null ||
            inputTokens is not null ||
            outputTokens is not null ||
            totalTokens is not null)
        {
            EnsureAssistant();
        }

        var normalizedFinishReason = NormalizeOptional(
            finishReason,
            MaximumFinishReasonLength,
            nameof(finishReason));
        ValidateUsage(inputTokens, outputTokens, totalTokens);

        TransitionTo(
            MessageStatus.Completed,
            updatedAtUtc,
            MessageStatus.Pending,
            MessageStatus.Streaming);

        FinishReason = normalizedFinishReason;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        TotalTokens = totalTokens;
        FailureReason = null;
        FailureKind = null;
        FailureStage = null;
        FailureStatusCode = null;
    }

    public void Fail(DateTime updatedAtUtc)
    {
        Fail(null, updatedAtUtc);
    }

    public void Fail(string? reason, DateTime updatedAtUtc)
    {
        Fail(
            reason,
            failureKind: null,
            failureStage: null,
            failureStatusCode: null,
            updatedAtUtc);
    }

    public void Fail(
        string? reason,
        string? failureKind,
        string? failureStage,
        int? failureStatusCode,
        DateTime updatedAtUtc)
    {
        if (failureKind is not null || failureStage is not null || failureStatusCode is not null)
        {
            EnsureAssistant();
        }

        var normalizedReason = NormalizeOptional(
            reason,
            MaximumFailureReasonLength,
            nameof(reason));
        var normalizedKind = NormalizeOptional(
            failureKind,
            MaximumFailureMetadataLength,
            nameof(failureKind));
        var normalizedStage = NormalizeOptional(
            failureStage,
            MaximumFailureMetadataLength,
            nameof(failureStage));

        if (failureStatusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureStatusCode),
                failureStatusCode,
                "Failure status code must be a valid HTTP status code.");
        }

        TransitionTo(
            MessageStatus.Failed,
            updatedAtUtc,
            MessageStatus.Pending,
            MessageStatus.Streaming);

        FailureReason = normalizedReason;
        FailureKind = normalizedKind;
        FailureStage = normalizedStage;
        FailureStatusCode = failureStatusCode;
        FinishReason = null;
        InputTokens = null;
        OutputTokens = null;
        TotalTokens = null;
    }

    public void Cancel(DateTime updatedAtUtc)
    {
        Cancel(finishReason: null, updatedAtUtc: updatedAtUtc);
    }

    public void Cancel(string? finishReason, DateTime updatedAtUtc)
    {
        if (finishReason is not null)
        {
            EnsureAssistant();
        }

        var normalizedFinishReason = NormalizeOptional(
            finishReason,
            MaximumFinishReasonLength,
            nameof(finishReason));

        TransitionTo(
            MessageStatus.Cancelled,
            updatedAtUtc,
            MessageStatus.Pending,
            MessageStatus.Streaming);

        FinishReason = normalizedFinishReason;
        FailureReason = null;
        FailureKind = null;
        FailureStage = null;
        FailureStatusCode = null;
        InputTokens = null;
        OutputTokens = null;
        TotalTokens = null;
    }


    private void EnsurePartCanBeAdded(MessagePartId id, int order)
    {
        if (_parts.Any(part => part.Id == id))
        {
            throw new InvalidOperationException($"Message part '{id}' already exists.");
        }

        var expectedOrder = _parts.Count + 1;
        if (order != expectedOrder)
        {
            throw new InvalidOperationException(
                $"Message part order must be '{expectedOrder}', but was '{order}'.");
        }
    }

    private void EnsureAssistant()
    {
        if (Role != MessageRole.Assistant)
        {
            throw new InvalidOperationException(
                "Only assistant messages can record model-run metadata.");
        }
    }

    private static void ValidateUsage(
        long? inputTokens,
        long? outputTokens,
        long? totalTokens)
    {
        if (inputTokens is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputTokens));
        }

        if (outputTokens is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputTokens));
        }

        if (totalTokens is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalTokens));
        }

        var suppliedCount = new[] { inputTokens, outputTokens, totalTokens }
            .Count(static value => value.HasValue);
        if (suppliedCount is not 0 and not 3)
        {
            throw new ArgumentException(
                "Usage token values must either all be present or all be absent.");
        }
    }

    private static string NormalizeRequired(
        string value,
        int maximumLength,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException(
                $"The value cannot exceed {maximumLength} characters.",
                parameterName);
        }

        return normalized;
    }

    private static string? NormalizeOptional(
        string? value,
        int maximumLength,
        string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        return NormalizeRequired(value, maximumLength, parameterName);
    }

    private void TransitionTo(
        MessageStatus target,
        DateTime updatedAtUtc,
        params MessageStatus[] allowedCurrentStatuses)
    {
        if (!allowedCurrentStatuses.Contains(Status))
        {
            throw new InvalidOperationException(
                $"Message cannot transition from '{Status}' to '{target}'.");
        }

        var validatedUpdatedAtUtc = UtcDateTime.EnsureNotBefore(
            updatedAtUtc,
            CreatedAtUtc,
            nameof(updatedAtUtc));

        Status = target;
        UpdatedAtUtc = validatedUpdatedAtUtc;
    }
}
