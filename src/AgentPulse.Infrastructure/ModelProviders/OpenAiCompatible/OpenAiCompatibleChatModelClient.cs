using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentPulse.Application.ChatModels;
using AgentPulse.Infrastructure.Credentials;

namespace AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

public sealed class OpenAiCompatibleChatModelClient(
    IHttpClientFactory httpClientFactory,
    OpenAiCompatibleModelOptions options,
    OpenAiCompatibleSseParser sseParser,
    IProviderCredentialSession credentialSession) : IChatModelClient
{
    private static readonly TimeSpan CredentialCleanupTimeout = TimeSpan.FromSeconds(1);

    public const string HttpClientName = "AgentPulse.OpenAiCompatible";

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ChatModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        options.Validate();

        string credential;
        try
        {
            credential = ProviderCredentialValidator.ValidateAndNormalize(
                credentialSession.GetRequiredCredential());
        }
        catch (ProviderCredentialValidationException exception)
        {
            throw new ModelProviderException(
                ModelProviderErrorCode.Authentication,
                exception.Message,
                ModelFailureStage.BeforeFirstToken,
                exception);
        }
        var requestDto = OpenAiCompatibleChatRequestMapper.Map(request, options);
        var requestJson = JsonSerializer.Serialize(requestDto);
        var endpoint = OpenAiCompatibleEndpointBuilder.Build(
            options.GetBaseUri(),
            options.ChatCompletionsPath);

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyAuthentication(requestMessage, credential);

        using var firstByteCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        firstByteCancellation.CancelAfter(options.FirstByteTimeout);

        HttpResponseMessage response;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            response = await client.SendAsync(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                firstByteCancellation.Token);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new ModelProviderOperationCanceledException(
                ModelFailureStage.BeforeFirstToken,
                cancellationToken,
                exception);
        }
        catch (OperationCanceledException exception)
        {
            throw CreateFirstByteTimeoutException(exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ModelProviderException(
                ModelProviderErrorCode.Unavailable,
                "The model endpoint could not be reached.",
                ModelFailureStage.BeforeFirstToken,
                exception);
        }

        using (response)
        {
            if (IsRedirect(response.StatusCode))
            {
                DisableFirstByteDeadline(firstByteCancellation);
                throw OpenAiCompatibleProviderErrorParser.CreateRedirectException(response, credential);
            }

            if (!response.IsSuccessStatusCode)
            {
                DisableFirstByteDeadline(firstByteCancellation);

                var credentialCleanup = response.StatusCode is
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? await TryMarkAuthenticationRejectedAsync(credentialSession)
                    : default;

                try
                {
                    throw await OpenAiCompatibleProviderErrorParser.CreateExceptionAsync(
                        response,
                        credential,
                        options.ErrorBodyReadTimeout,
                        cancellationToken,
                        credentialCleanup.Failed,
                        credentialCleanup.TimedOut);
                }
                catch (OperationCanceledException exception)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw new ModelProviderOperationCanceledException(
                        ModelFailureStage.BeforeFirstToken,
                        cancellationToken,
                        exception);
                }
            }

            Stream responseStream;
            try
            {
                responseStream = await response.Content.ReadAsStreamAsync(
                    firstByteCancellation.Token);
            }
            catch (OperationCanceledException exception)
                when (cancellationToken.IsCancellationRequested)
            {
                throw new ModelProviderOperationCanceledException(
                    ModelFailureStage.BeforeFirstToken,
                    cancellationToken,
                    exception);
            }
            catch (OperationCanceledException exception)
            {
                throw CreateFirstByteTimeoutException(exception);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                throw new ModelProviderException(
                    ModelProviderErrorCode.Unavailable,
                    "The model response stream could not be opened.",
                    ModelFailureStage.BeforeFirstToken,
                    exception);
            }

            await using var timedStream = new DeadlineReadStream(
                responseStream,
                firstByteCancellation.Token,
                options.FirstByteTimeout,
                options.StreamIdleTimeout,
                () => DisableFirstByteDeadline(firstByteCancellation));
            await using var enumerator = sseParser
                .ParseAsync(timedStream, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            var firstTokenSeen = false;
            var credentialAccepted = false;

            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (ModelProviderException exception)
                {
                    throw exception.WithFailureStage(GetFailureStage(firstTokenSeen));
                }
                catch (OperationCanceledException exception)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw new ModelProviderOperationCanceledException(
                        GetFailureStage(firstTokenSeen),
                        cancellationToken,
                        exception);
                }
                catch (Exception exception) when (exception is IOException or HttpRequestException)
                {
                    throw new ModelProviderException(
                        ModelProviderErrorCode.Unavailable,
                        "The model response stream ended because of a network failure.",
                        GetFailureStage(firstTokenSeen),
                        exception);
                }

                if (!hasNext)
                {
                    break;
                }

                var streamEvent = enumerator.Current;
                if (streamEvent is ModelStreamEvent.TextDelta)
                {
                    firstTokenSeen = true;
                }

                if (!credentialAccepted)
                {
                    try
                    {
                        await credentialSession.MarkAcceptedAsync(cancellationToken);
                        credentialAccepted = true;
                    }
                    catch (OperationCanceledException exception)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        throw new ModelProviderOperationCanceledException(
                            GetFailureStage(firstTokenSeen),
                            cancellationToken,
                            exception);
                    }
                }

                yield return streamEvent;
            }
        }
    }


    public async Task<ChatModelResponse> CompleteAsync(
        ChatModelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        options.Validate();

        string credential;
        try
        {
            credential = ProviderCredentialValidator.ValidateAndNormalize(
                credentialSession.GetRequiredCredential());
        }
        catch (ProviderCredentialValidationException exception)
        {
            throw new ModelProviderException(
                ModelProviderErrorCode.Authentication,
                exception.Message,
                ModelFailureStage.BeforeFirstToken,
                exception);
        }

        var requestJson = JsonSerializer.Serialize(
            OpenAiCompatibleChatRequestMapper.Map(request, options, stream: false));
        var endpoint = OpenAiCompatibleEndpointBuilder.Build(
            options.GetBaseUri(),
            options.ChatCompletionsPath);
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        ApplyAuthentication(requestMessage, credential);

        using var firstByteCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        firstByteCancellation.CancelAfter(options.FirstByteTimeout);

        HttpResponseMessage response;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            response = await client.SendAsync(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                firstByteCancellation.Token);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new ModelProviderOperationCanceledException(
                ModelFailureStage.BeforeFirstToken,
                cancellationToken,
                exception);
        }
        catch (OperationCanceledException exception)
        {
            throw CreateFirstByteTimeoutException(exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ModelProviderException(
                ModelProviderErrorCode.Unavailable,
                "The model endpoint could not be reached.",
                ModelFailureStage.BeforeFirstToken,
                exception);
        }

        using (response)
        {
            if (IsRedirect(response.StatusCode))
            {
                DisableFirstByteDeadline(firstByteCancellation);
                throw OpenAiCompatibleProviderErrorParser.CreateRedirectException(response, credential);
            }

            if (!response.IsSuccessStatusCode)
            {
                DisableFirstByteDeadline(firstByteCancellation);
                var credentialCleanup = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? await TryMarkAuthenticationRejectedAsync(credentialSession)
                    : default;
                try
                {
                    throw await OpenAiCompatibleProviderErrorParser.CreateExceptionAsync(
                        response,
                        credential,
                        options.ErrorBodyReadTimeout,
                        cancellationToken,
                        credentialCleanup.Failed,
                        credentialCleanup.TimedOut);
                }
                catch (OperationCanceledException exception)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw new ModelProviderOperationCanceledException(
                        ModelFailureStage.BeforeFirstToken,
                        cancellationToken,
                        exception);
                }
            }

            Stream responseStream;
            try
            {
                responseStream = await response.Content.ReadAsStreamAsync(
                    firstByteCancellation.Token);
            }
            catch (OperationCanceledException exception)
                when (cancellationToken.IsCancellationRequested)
            {
                throw new ModelProviderOperationCanceledException(
                    ModelFailureStage.BeforeFirstToken,
                    cancellationToken,
                    exception);
            }
            catch (OperationCanceledException exception)
            {
                throw CreateFirstByteTimeoutException(exception);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                throw new ModelProviderException(
                    ModelProviderErrorCode.Unavailable,
                    "The model response stream could not be opened.",
                    ModelFailureStage.BeforeFirstToken,
                    exception);
            }

            await using var timedStream = new DeadlineReadStream(
                responseStream,
                firstByteCancellation.Token,
                options.FirstByteTimeout,
                options.StreamIdleTimeout,
                () => DisableFirstByteDeadline(firstByteCancellation));

            ChatModelResponse completion;
            try
            {
                completion = IsEventStream(response)
                    ? await ParseStreamingCompletionAsync(timedStream, cancellationToken)
                    : ParseCompletionResponse(
                        await ReadCompletionBodyAsync(timedStream, cancellationToken));
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new ModelProviderOperationCanceledException(
                    ModelFailureStage.BeforeFirstToken,
                    cancellationToken,
                    exception);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                throw new ModelProviderException(
                    ModelProviderErrorCode.Unavailable,
                    "The model response ended because of a network failure.",
                    ModelFailureStage.BeforeFirstToken,
                    exception);
            }

            try
            {
                await credentialSession.MarkAcceptedAsync(cancellationToken);
            }
            catch (OperationCanceledException exception)
                when (cancellationToken.IsCancellationRequested)
            {
                throw new ModelProviderOperationCanceledException(
                    ModelFailureStage.BeforeFirstToken,
                    cancellationToken,
                    exception);
            }

            return completion;
        }
    }



    private async Task<ChatModelResponse> ParseStreamingCompletionAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        ModelFinishReason finishReason = ModelFinishReason.Unknown;
        ModelUsage? usage = null;

        await foreach (var streamEvent in sseParser
                           .ParseAsync(stream, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            switch (streamEvent)
            {
                case ModelStreamEvent.TextDelta delta:
                    text.Append(delta.Text);
                    break;
                case ModelStreamEvent.Completed completed:
                    finishReason = completed.FinishReason;
                    break;
                case ModelStreamEvent.Usage usageEvent:
                    usage = usageEvent.Value;
                    break;
                case ModelStreamEvent.Failed failed:
                    throw new ModelProviderException(
                        ModelProviderErrorCode.InvalidResponse,
                        failed.ErrorMessage,
                        ModelFailureStage.BeforeFirstToken);
            }
        }

        if (text.Length == 0)
        {
            throw new ModelProviderException(
                ModelProviderErrorCode.InvalidResponse,
                "The model endpoint returned an empty streaming completion.",
                ModelFailureStage.BeforeFirstToken);
        }

        return new ChatModelResponse(text.ToString(), [], finishReason, usage);
    }

    private static async Task<string> ReadCompletionBodyAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static bool IsEventStream(HttpResponseMessage response)
    {
        return string.Equals(
            response.Content.Headers.ContentType?.MediaType,
            "text/event-stream",
            StringComparison.OrdinalIgnoreCase);
    }

    private static ChatModelResponse ParseCompletionResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                throw new JsonException("The response does not contain a choice.");
            }

            var choice = choices[0];
            if (!choice.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The response choice does not contain a message.");
            }

            string? text = null;
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                text = content.GetString();
            }

            var calls = new List<ChatModelToolCall>();
            if (message.TryGetProperty("tool_calls", out var toolCalls) &&
                toolCalls.ValueKind == JsonValueKind.Array)
            {
                var order = 1;
                foreach (var item in toolCalls.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString();
                    var function = item.GetProperty("function");
                    var name = function.GetProperty("name").GetString();
                    var arguments = function.GetProperty("arguments").GetString() ?? "{}";
                    calls.Add(new ChatModelToolCall(
                        id ?? throw new JsonException("Tool call id is missing."),
                        name ?? throw new JsonException("Tool call name is missing."),
                        arguments,
                        order++));
                }
            }

            var finishReason = choice.TryGetProperty("finish_reason", out var finish)
                ? MapFinishReason(finish.GetString())
                : calls.Count > 0 ? ModelFinishReason.ToolCalls : ModelFinishReason.Unknown;
            ModelUsage? usage = null;
            if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
            {
                var input = ReadLong(usageElement, "prompt_tokens");
                var output = ReadLong(usageElement, "completion_tokens");
                var total = ReadLong(usageElement, "total_tokens");
                if (input is not null && output is not null && total is not null)
                {
                    usage = new ModelUsage(input.Value, output.Value, total.Value);
                }
            }

            return new ChatModelResponse(text, calls, finishReason, usage);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new ModelProviderException(
                ModelProviderErrorCode.InvalidResponse,
                "The model endpoint returned an invalid completion response.",
                ModelFailureStage.BeforeFirstToken,
                exception);
        }
    }

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;
    }

    private static ModelFinishReason MapFinishReason(string? value)
    {
        return value switch
        {
            "stop" => ModelFinishReason.Stop,
            "length" => ModelFinishReason.Length,
            "tool_calls" => ModelFinishReason.ToolCalls,
            null => ModelFinishReason.Unknown,
            _ => ModelFinishReason.Unknown,
        };
    }

    private static async Task<CredentialCleanupResult> TryMarkAuthenticationRejectedAsync(
        IProviderCredentialSession session)
    {
        using var cleanupCancellation = new CancellationTokenSource(CredentialCleanupTimeout);

        try
        {
            await session
                .MarkAuthenticationRejectedAsync(cleanupCancellation.Token)
                .WaitAsync(cleanupCancellation.Token);
            return default;
        }
        catch (OperationCanceledException) when (cleanupCancellation.IsCancellationRequested)
        {
            return new CredentialCleanupResult(Failed: true, TimedOut: true);
        }
        catch (Exception exception) when (!IsFatalException(exception))
        {
            return new CredentialCleanupResult(Failed: true, TimedOut: false);
        }
    }

    private static bool IsFatalException(Exception exception)
    {
        return exception is
            OutOfMemoryException or
            StackOverflowException or
            AccessViolationException or
            AppDomainUnloadedException or
            BadImageFormatException or
            CannotUnloadAppDomainException or
            System.Runtime.InteropServices.SEHException;
    }

    private readonly record struct CredentialCleanupResult(bool Failed, bool TimedOut);

    private void ApplyAuthentication(HttpRequestMessage requestMessage, string credential)
    {
        switch (options.AuthenticationMode)
        {
            case OpenAiCompatibleAuthenticationMode.Bearer:
                requestMessage.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    credential);
                break;
            case OpenAiCompatibleAuthenticationMode.ApiKeyHeader:
                if (!requestMessage.Headers.TryAddWithoutValidation(
                        options.ApiKeyHeaderName.Trim(),
                        credential))
                {
                    throw new ModelProviderException(
                        ModelProviderErrorCode.Authentication,
                        "The configured API credential header could not be added safely.",
                        ModelFailureStage.BeforeFirstToken);
                }

                break;
            default:
                throw new InvalidOperationException(
                    "The configured authentication mode is not supported.");
        }
    }

    private ModelProviderException CreateFirstByteTimeoutException(Exception exception)
    {
        return new ModelProviderException(
            ModelProviderErrorCode.Timeout,
            $"The model endpoint did not send the first response byte before the configured timeout of {options.FirstByteTimeout}.",
            ModelFailureStage.BeforeFirstToken,
            exception);
    }

    private static void DisableFirstByteDeadline(CancellationTokenSource source)
    {
        source.CancelAfter(Timeout.InfiniteTimeSpan);
    }

    private static ModelFailureStage GetFailureStage(bool firstTokenSeen)
    {
        return firstTokenSeen
            ? ModelFailureStage.AfterFirstToken
            : ModelFailureStage.BeforeFirstToken;
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is
            HttpStatusCode.MovedPermanently or
            HttpStatusCode.Found or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;
    }
}
