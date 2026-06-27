using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentPulse.Application.ChatModels;
using AgentPulse.Infrastructure.Credentials;

namespace AgentPulse.Infrastructure.ModelProviders.Xiaomi;

public sealed class XiaomiChatModelClient(
    IHttpClientFactory httpClientFactory,
    XiaomiModelOptions options,
    XiaomiSseParser sseParser,
    IProviderCredentialSession credentialSession) : IChatModelClient
{
    public const string HttpClientName = "AgentPulse.XiaomiMiMo";
    private const int MaximumErrorBodyCharacters = 2048;

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ChatModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        options.Validate();

        var credential = credentialSession.GetRequiredCredential();
        var requestDto = XiaomiChatRequestMapper.Map(request, options);
        var requestJson = JsonSerializer.Serialize(requestDto);
        var endpoint = BuildEndpoint(options.BaseUrl);

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        requestMessage.Headers.TryAddWithoutValidation("api-key", credential);

        HttpResponseMessage response;

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            response = await client.SendAsync(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new ModelProviderException(
                ModelProviderErrorCode.ConnectionFailed,
                "Xiaomi MiMo could not be reached.",
                exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorSnippet = await ReadErrorSnippetAsync(
                    response,
                    credential,
                    cancellationToken);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    await credentialSession.MarkAuthenticationRejectedAsync(
                        CancellationToken.None);
                    throw new ModelProviderException(
                        ModelProviderErrorCode.Authentication,
                        $"Xiaomi MiMo authentication failed with HTTP {(int)response.StatusCode}." +
                        FormatErrorSuffix(errorSnippet));
                }

                var errorCode = response.StatusCode == HttpStatusCode.TooManyRequests
                    ? ModelProviderErrorCode.RateLimited
                    : (int)response.StatusCode >= 500
                        ? ModelProviderErrorCode.ServiceUnavailable
                        : ModelProviderErrorCode.InvalidResponse;

                throw new ModelProviderException(
                    errorCode,
                    $"Xiaomi MiMo returned HTTP {(int)response.StatusCode}." +
                    FormatErrorSuffix(errorSnippet));
            }

            await credentialSession.MarkAcceptedAsync(cancellationToken);

            Stream responseStream;
            try
            {
                responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                throw new ModelProviderException(
                    ModelProviderErrorCode.ConnectionFailed,
                    "Xiaomi MiMo response streaming could not be started.",
                    exception);
            }

            await using var timedStream = new TimedReadStream(
                responseStream,
                options.FirstByteTimeout,
                options.StreamIdleTimeout);

            await foreach (var streamEvent in sseParser
                               .ParseAsync(timedStream, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                yield return streamEvent;
            }
        }
    }

    private static Uri BuildEndpoint(string baseUrl)
    {
        var normalizedBaseUrl = baseUrl.TrimEnd('/') + "/";
        return new Uri(new Uri(normalizedBaseUrl, UriKind.Absolute), "chat/completions");
    }

    private static async Task<string> ReadErrorSnippetAsync(
        HttpResponseMessage response,
        string credential,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 512,
                leaveOpen: false);
            var buffer = new char[MaximumErrorBodyCharacters];
            var read = await reader.ReadBlockAsync(
                buffer.AsMemory(0, buffer.Length),
                cancellationToken);
            var value = new string(buffer, 0, read);
            return Redact(value, credential);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or HttpRequestException or DecoderFallbackException)
        {
            return "The provider error body could not be read.";
        }
    }

    private static string Redact(string value, string credential)
    {
        var redacted = value.Replace(
            credential,
            "[REDACTED]",
            StringComparison.Ordinal);
        redacted = redacted.Replace(
            "api-key",
            "[REDACTED-HEADER]",
            StringComparison.OrdinalIgnoreCase);
        redacted = redacted.Replace(
            "authorization",
            "[REDACTED-HEADER]",
            StringComparison.OrdinalIgnoreCase);
        return redacted.Trim();
    }

    private static string FormatErrorSuffix(string errorSnippet)
    {
        return string.IsNullOrWhiteSpace(errorSnippet)
            ? string.Empty
            : $" {errorSnippet}";
    }
}
