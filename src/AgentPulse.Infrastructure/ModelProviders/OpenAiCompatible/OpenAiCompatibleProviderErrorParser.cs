using System.Net;
using System.Text;
using System.Text.Json;
using AgentPulse.Application.ChatModels;

namespace AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

internal static class OpenAiCompatibleProviderErrorParser
{
    private const int MaximumErrorBodyBytes = 16 * 1024;
    private const int MaximumMetadataLength = 128;

    public static async Task<ModelProviderException> CreateExceptionAsync(
        HttpResponseMessage response,
        string credential,
        TimeSpan readTimeout,
        CancellationToken cancellationToken,
        bool credentialCleanupFailed = false,
        bool credentialCleanupTimedOut = false)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);

        var statusCode = response.StatusCode;
        var code = MapStatusCode(statusCode);
        var body = await ReadLimitedBodyAsync(response, readTimeout, cancellationToken);
        var parsed = body.TimedOut ? default : ParseBody(body.Value);
        var message = GetPublicMessage(code);

        if (body.TimedOut)
        {
            message += " Provider error details could not be read before the configured timeout.";
        }
        else if (body.Truncated)
        {
            message += " Provider error details were truncated.";
        }

        return new ModelProviderException(
            code,
            message,
            ModelFailureStage.BeforeFirstToken,
            httpStatusCode: statusCode,
            providerErrorCode: SanitizeIdentifier(parsed.Code, credential),
            providerErrorType: SanitizeIdentifier(parsed.Type, credential),
            retryAfter: GetRetryAfter(response),
            requestId: GetRequestId(response, credential),
            errorBodyReadTimedOut: body.TimedOut,
            credentialCleanupFailed: credentialCleanupFailed,
            credentialCleanupTimedOut: credentialCleanupTimedOut);
    }

    public static ModelProviderException CreateRedirectException(
        HttpResponseMessage response,
        string credential)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);

        var location = response.Headers.Location;
        var safeLocation = location is null
            ? null
            : SanitizeLocation(location).Replace(
                credential,
                "[REDACTED]",
                StringComparison.Ordinal);
        var message = safeLocation is null
            ? $"The model provider returned redirect HTTP {(int)response.StatusCode}; redirects are not followed."
            : $"The model provider returned redirect HTTP {(int)response.StatusCode} to '{safeLocation}'; redirects are not followed.";

        return new ModelProviderException(
            ModelProviderErrorCode.InvalidResponse,
            message,
            ModelFailureStage.BeforeFirstToken,
            httpStatusCode: response.StatusCode,
            requestId: GetRequestId(response, credential));
    }

    private static async Task<ErrorBody> ReadLimitedBodyAsync(
        HttpResponseMessage response,
        TimeSpan readTimeout,
        CancellationToken cancellationToken)
    {
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        readCancellation.CancelAfter(readTimeout);

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(
                readCancellation.Token);
            var buffer = new byte[4096];
            using var output = new MemoryStream();
            var truncated = false;

            while (output.Length <= MaximumErrorBodyBytes)
            {
                var remaining = MaximumErrorBodyBytes + 1 - (int)output.Length;
                if (remaining <= 0)
                {
                    truncated = true;
                    break;
                }

                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                    readCancellation.Token);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), readCancellation.Token);
            }

            if (output.Length > MaximumErrorBodyBytes)
            {
                truncated = true;
                output.SetLength(MaximumErrorBodyBytes);
            }

            var value = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: false)
                .GetString(output.ToArray());
            return new ErrorBody(value, truncated, TimedOut: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ErrorBody(string.Empty, Truncated: false, TimedOut: true);
        }
        catch (Exception exception) when (
            exception is IOException or HttpRequestException)
        {
            return new ErrorBody(string.Empty, Truncated: false, TimedOut: false);
        }
    }

    private static ParsedError ParseBody(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            var error = document.RootElement.TryGetProperty("error", out var wrapped) &&
                        wrapped.ValueKind == JsonValueKind.Object
                ? wrapped
                : document.RootElement;
            return new ParsedError(
                GetScalar(error, "type"),
                GetScalar(error, "code"));
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? GetScalar(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();
    }

    private static string GetPublicMessage(ModelProviderErrorCode code)
    {
        return code switch
        {
            ModelProviderErrorCode.Authentication =>
                "The model provider rejected the API credential.",
            ModelProviderErrorCode.PermissionDenied =>
                "The model provider denied access to the requested resource.",
            ModelProviderErrorCode.RateLimited =>
                "The model provider rate limit was exceeded.",
            ModelProviderErrorCode.InvalidRequest =>
                "The model provider rejected the request.",
            ModelProviderErrorCode.Unavailable =>
                "The model provider is temporarily unavailable.",
            ModelProviderErrorCode.Timeout =>
                "The model provider request timed out.",
            ModelProviderErrorCode.Protocol =>
                "The model provider returned an invalid protocol response.",
            _ => "The model provider returned an invalid response.",
        };
    }

    private static ModelProviderErrorCode MapStatusCode(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => ModelProviderErrorCode.InvalidRequest,
            HttpStatusCode.Unauthorized => ModelProviderErrorCode.Authentication,
            HttpStatusCode.Forbidden => ModelProviderErrorCode.PermissionDenied,
            HttpStatusCode.RequestTimeout => ModelProviderErrorCode.Timeout,
            HttpStatusCode.Conflict => ModelProviderErrorCode.InvalidRequest,
            HttpStatusCode.TooManyRequests => ModelProviderErrorCode.RateLimited,
            _ when (int)statusCode >= 500 => ModelProviderErrorCode.Unavailable,
            _ => ModelProviderErrorCode.InvalidResponse,
        };
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }

        return null;
    }

    private static string? GetRequestId(
        HttpResponseMessage response,
        string credential)
    {
        foreach (var headerName in new[] { "x-request-id", "request-id", "x-correlation-id" })
        {
            if (!response.Headers.TryGetValues(headerName, out var values))
            {
                continue;
            }

            return SanitizeIdentifier(values.FirstOrDefault(), credential);
        }

        return null;
    }

    private static string? SanitizeIdentifier(string? value, string credential)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains(credential, StringComparison.Ordinal))
        {
            return null;
        }

        var normalized = value.Trim(' ');
        if (normalized.Length == 0 || normalized.Length > MaximumMetadataLength)
        {
            return null;
        }

        return normalized.All(static character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '_' or '-' or '.' or ':' or '/')
            ? normalized
            : null;
    }

    private static string SanitizeLocation(Uri location)
    {
        if (!location.IsAbsoluteUri)
        {
            return location.GetComponents(UriComponents.Path, UriFormat.SafeUnescaped);
        }

        var builder = new UriBuilder(location)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty,
        };
        return builder.Uri.GetLeftPart(UriPartial.Path);
    }

    private readonly record struct ErrorBody(
        string Value,
        bool Truncated,
        bool TimedOut);

    private readonly record struct ParsedError(string? Type, string? Code);
}
