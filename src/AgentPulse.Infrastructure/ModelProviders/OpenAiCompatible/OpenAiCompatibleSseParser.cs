using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentPulse.Application.ChatModels;

namespace AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

public sealed class OpenAiCompatibleSseParser
{
    public async IAsyncEnumerable<ModelStreamEvent> ParseAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var utf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        using var reader = new StreamReader(
            stream,
            utf8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);

        var state = new ParserState();
        var dataLines = new List<string>();

        while (true)
        {
            var line = await ReadLineAsync(reader, cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (dataLines.Count == 0)
                {
                    continue;
                }

                var payload = string.Join('\n', dataLines);
                dataLines.Clear();

                foreach (var streamEvent in ParsePayload(payload, state))
                {
                    yield return streamEvent;
                }

                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':');
            var field = separatorIndex < 0
                ? line
                : line[..separatorIndex];
            var value = separatorIndex < 0
                ? string.Empty
                : line[(separatorIndex + 1)..];

            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            if (!string.Equals(field, "data", StringComparison.Ordinal))
            {
                continue;
            }

            if (state.Done)
            {
                throw InvalidResponse(
                    "The OpenAI-compatible provider stream produced an event after completion.");
            }

            dataLines.Add(value);
        }

        if (dataLines.Count > 0)
        {
            var payload = string.Join('\n', dataLines);

            foreach (var streamEvent in ParsePayload(payload, state))
            {
                yield return streamEvent;
            }
        }

        if (!state.Done)
        {
            throw InvalidResponse(
                "The OpenAI-compatible provider stream ended before the [DONE] marker.");
        }
    }

    private static async ValueTask<string?> ReadLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadLineAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DecoderFallbackException exception)
        {
            throw new ModelProviderException(
                ModelProviderErrorCode.InvalidResponse,
                "The OpenAI-compatible provider stream contained invalid UTF-8 data.",
                exception);
        }
    }

    private static IReadOnlyList<ModelStreamEvent> ParsePayload(
        string payload,
        ParserState state)
    {
        if (string.Equals(payload.Trim(), "[DONE]", StringComparison.Ordinal))
        {
            if (state.Done)
            {
                throw InvalidResponse(
                    "The OpenAI-compatible provider stream completed more than once.");
            }

            if (!state.FinishReasonSeen || state.FinishReason is null)
            {
                throw InvalidResponse(
                    "The OpenAI-compatible provider stream ended without a finish reason.");
            }

            if (!state.TextSeen)
            {
                throw InvalidResponse(
                    "The OpenAI-compatible provider stream completed without usable text output.");
            }

            state.Done = true;
            return [new ModelStreamEvent.Completed(state.FinishReason.Value)];
        }

        if (state.Done)
        {
            throw InvalidResponse(
                "The OpenAI-compatible provider stream produced an event after completion.");
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new ModelProviderException(
                ModelProviderErrorCode.InvalidResponse,
                "The OpenAI-compatible provider stream contained invalid JSON.",
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidResponse(
                    "The OpenAI-compatible provider stream contained an unexpected JSON value.");
            }

            var events = new List<ModelStreamEvent>();
            var recognized = false;

            if (root.TryGetProperty("choices", out var choices))
            {
                recognized = true;

                if (choices.ValueKind != JsonValueKind.Array)
                {
                    throw InvalidResponse(
                        "The OpenAI-compatible provider stream choices value was not an array.");
                }

                if (choices.GetArrayLength() > 0)
                {
                    if (state.FinishReasonSeen)
                    {
                        throw InvalidResponse(
                            "The OpenAI-compatible provider stream produced a choice after its finish reason.");
                    }

                    ParseChoice(choices[0], state, events);
                }
            }

            if (root.TryGetProperty("usage", out var usage) &&
                usage.ValueKind != JsonValueKind.Null)
            {
                recognized = true;
                events.Add(new ModelStreamEvent.Usage(ParseUsage(usage)));
            }

            if (!recognized)
            {
                throw InvalidResponse(
                    "The OpenAI-compatible provider stream contained an unknown JSON event.");
            }

            return events;
        }
    }

    private static void ParseChoice(
        JsonElement choice,
        ParserState state,
        ICollection<ModelStreamEvent> events)
    {
        if (choice.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse(
                "The OpenAI-compatible provider stream contained an invalid choice.");
        }

        if (choice.TryGetProperty("delta", out var delta) &&
            delta.ValueKind != JsonValueKind.Null)
        {
            if (delta.ValueKind != JsonValueKind.Object)
            {
                throw InvalidResponse(
                    "The OpenAI-compatible provider stream contained an invalid delta.");
            }

            if (delta.TryGetProperty("tool_calls", out var toolCalls) &&
                HasUnsupportedToolCalls(toolCalls))
            {
                throw new ModelProviderException(
                    ModelProviderErrorCode.UnsupportedFeature,
                    "The OpenAI-compatible provider returned tool calls, which are not supported in this phase.");
            }

            if (delta.TryGetProperty("content", out var content) &&
                content.ValueKind != JsonValueKind.Null)
            {
                if (content.ValueKind != JsonValueKind.String)
                {
                    throw InvalidResponse(
                        "The OpenAI-compatible provider stream returned non-text content.");
                }

                var text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    state.TextSeen = true;
                    events.Add(new ModelStreamEvent.TextDelta(text));
                }
            }
        }

        if (choice.TryGetProperty("finish_reason", out var finishReason) &&
            finishReason.ValueKind != JsonValueKind.Null)
        {
            if (finishReason.ValueKind != JsonValueKind.String)
            {
                throw InvalidResponse(
                    "The OpenAI-compatible provider stream returned an invalid finish reason.");
            }

            if (state.FinishReasonSeen)
            {
                throw InvalidResponse(
                    "The OpenAI-compatible provider stream returned more than one finish reason.");
            }

            state.FinishReasonSeen = true;
            state.FinishReason = MapFinishReason(finishReason.GetString());
        }
    }

    private static ModelUsage ParseUsage(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object ||
            !TryGetNonNegativeInt64(usage, "prompt_tokens", out var promptTokens) ||
            !TryGetNonNegativeInt64(usage, "completion_tokens", out var completionTokens) ||
            !TryGetNonNegativeInt64(usage, "total_tokens", out var totalTokens))
        {
            throw InvalidResponse(
                "The OpenAI-compatible provider stream returned invalid usage information.");
        }

        try
        {
            return new ModelUsage(promptTokens, completionTokens, totalTokens);
        }
        catch (ArgumentException exception)
        {
            throw new ModelProviderException(
                ModelProviderErrorCode.InvalidResponse,
                "The OpenAI-compatible provider stream returned inconsistent usage information.",
                exception);
        }
    }

    private static bool TryGetNonNegativeInt64(
        JsonElement value,
        string propertyName,
        out long result)
    {
        result = 0;
        return value.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out result) &&
               result >= 0;
    }

    private static bool HasUnsupportedToolCalls(JsonElement toolCalls)
    {
        return toolCalls.ValueKind switch
        {
            JsonValueKind.Null => false,
            JsonValueKind.Array => toolCalls.GetArrayLength() > 0,
            JsonValueKind.Undefined => false,
            _ => true,
        };
    }

    private static ModelFinishReason MapFinishReason(string? reason)
    {
        return reason switch
        {
            "stop" => ModelFinishReason.Stop,
            "length" => ModelFinishReason.Length,
            "cancelled" => ModelFinishReason.Cancelled,
            "error" => ModelFinishReason.Error,
            _ => ModelFinishReason.Unknown,
        };
    }

    private static ModelProviderException InvalidResponse(string message)
    {
        return new ModelProviderException(
            ModelProviderErrorCode.InvalidResponse,
            message);
    }

    private sealed class ParserState
    {
        public bool TextSeen { get; set; }

        public bool FinishReasonSeen { get; set; }

        public ModelFinishReason? FinishReason { get; set; }

        public bool Done { get; set; }
    }
}
