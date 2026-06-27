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

        var credential = credentialSession.GetRequiredCredential();
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

        HttpResponseMessage response;
        using (var firstByteCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken))
        {
            firstByteCancellation.CancelAfter(options.FirstByteTimeout);
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
                throw new ModelProviderException(
                    ModelProviderErrorCode.Timeout,
                    $"The model endpoint did not respond before the first-byte timeout of {options.FirstByteTimeout}.",
                    ModelFailureStage.BeforeFirstToken,
                    exception);
            }
            catch (HttpRequestException exception)
            {
                throw new ModelProviderException(
                    ModelProviderErrorCode.Unavailable,
                    "The model endpoint could not be reached.",
                    ModelFailureStage.BeforeFirstToken,
                    exception);
            }
        }

        using (response)
        {
            if (IsRedirect(response.StatusCode))
            {
                throw OpenAiCompatibleProviderErrorParser.CreateRedirectException(response, credential);
            }

            if (!response.IsSuccessStatusCode)
            {
                ModelProviderException error;
                try
                {
                    error = await OpenAiCompatibleProviderErrorParser.CreateExceptionAsync(
                        response,
                        credential,
                        request,
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

            Stream responseStream;
            try
            {
                responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            }
            catch (OperationCanceledException exception)
                when (cancellationToken.IsCancellationRequested)
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
                    "The model response stream could not be opened.",
                    ModelFailureStage.BeforeFirstToken,
                    exception);
            }

            await using var timedStream = new TimedReadStream(
                responseStream,
                options.FirstByteTimeout,
                options.StreamIdleTimeout);
            await using var enumerator = sseParser
                .ParseAsync(timedStream, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            var firstTokenSeen = false;

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
                    throw new InvalidOperationException(
                        "The configured API key header could not be added to the request.");
                }

                break;
            default:
                throw new InvalidOperationException(
                    "The configured authentication mode is not supported.");
        }
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
