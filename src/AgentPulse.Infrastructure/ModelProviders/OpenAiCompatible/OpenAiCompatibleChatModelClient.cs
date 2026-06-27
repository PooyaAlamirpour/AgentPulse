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
    public const string HttpClientName = "AgentPulse.OpenAiCompatible";

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ChatModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        options.Validate();

        var credential = OpenAiCompatibleCredentialValidator.ValidateAndNormalize(
            credentialSession.GetRequiredCredential());
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
                ModelProviderException error;
                try
                {
                    error = await OpenAiCompatibleProviderErrorParser.CreateExceptionAsync(
                        response,
                        credential,
                        request,
                        options.ErrorBodyReadTimeout,
                        cancellationToken);
                }
                catch (OperationCanceledException exception)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw new ModelProviderOperationCanceledException(
                        ModelFailureStage.BeforeFirstToken,
                        cancellationToken,
                        exception);
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    await credentialSession.MarkAuthenticationRejectedAsync(CancellationToken.None);
                }

                throw error;
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
