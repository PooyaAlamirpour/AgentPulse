using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentPulse.Application.ChatModels;

namespace AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

internal static partial class OpenAiCompatibleProviderErrorParser
{
    private const int MaximumErrorBodyBytes = 16 * 1024;
    private const int MaximumSafeMessageLength = 2048;
    private const int MaximumMetadataLength = 128;

    public static async Task<ModelProviderException> CreateExceptionAsync(
        HttpResponseMessage response,
        string credential,
        ChatModelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        ArgumentNullException.ThrowIfNull(request);

        var body = await ReadLimitedBodyAsync(response, cancellationToken);
        var parsed = ParseBody(body.Value);
        var sensitiveValues = request.Messages
            .Select(static message => message.Content)
            .Where(static content => !string.IsNullOrEmpty(content))
            .Append(credential)
            .ToArray();
        var safeProviderMessage = Sanitize(parsed.Message ?? body.Value, sensitiveValues);
        var statusCode = response.StatusCode;
        var code = MapStatusCode(statusCode);
        var statusMessage = $"The model endpoint returned HTTP {(int)statusCode}.";
        var message = string.IsNullOrWhiteSpace(safeProviderMessage)
            ? statusMessage
            : $"{statusMessage} {safeProviderMessage}";

        if (body.Truncated)
        {
            message += " The provider error body was truncated.";
        }

        return new ModelProviderException(
            code,
            message,
            ModelFailureStage.BeforeFirstToken,
            httpStatusCode: statusCode,
            providerErrorCode: SanitizeMetadata(parsed.Code, sensitiveValues),
            providerErrorType: SanitizeMetadata(parsed.Type, sensitiveValues),
            retryAfter: GetRetryAfter(response),
            requestId: GetRequestId(response, sensitiveValues));
    }

    public static ModelProviderException CreateRedirectException(
        HttpResponseMessage response,
        string credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        var location = response.Headers.Location;
        var safeLocation = location is null
            ? null
            : SanitizeLocation(location).Replace(
                credential,
                "[REDACTED]",
                StringComparison.Ordinal);
        var message = safeLocation is null
            ? $"The model endpoint returned redirect HTTP {(int)response.StatusCode}; redirects are not followed."
            : $"The model endpoint returned redirect HTTP {(int)response.StatusCode} to '{safeLocation}'; redirects are not followed.";

        return new ModelProviderException(
            ModelProviderErrorCode.InvalidResponse,
            message,
            ModelFailureStage.BeforeFirstToken,
            httpStatusCode: response.StatusCode,
            requestId: GetRequestId(response, [credential]));
    }

    private static async Task<ErrorBody> ReadLimitedBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
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
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
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
            return new ErrorBody(value, truncated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or HttpRequestException)
        {
            return new ErrorBody(
                $"The provider error body could not be read ({exception.GetType().Name}).",
                Truncated: false);
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
                return new ParsedError(value, null, null);
            }

            var error = document.RootElement.TryGetProperty("error", out var wrapped) &&
                        wrapped.ValueKind == JsonValueKind.Object
                ? wrapped
                : document.RootElement;
            return new ParsedError(
                GetString(error, "message"),
                GetScalar(error, "type"),
                GetScalar(error, "code"));
        }
        catch (JsonException)
        {
            return new ParsedError(value, null, null);
        }
    }

    private static string? GetString(JsonElement value, string propertyName)
    {
        return value.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
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
        IReadOnlyCollection<string> sensitiveValues)
    {
        foreach (var headerName in new[] { "x-request-id", "request-id", "x-correlation-id" })
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                var value = values.FirstOrDefault();
                if (value is null)
                {
                    return null;
                }

                foreach (var sensitiveValue in sensitiveValues)
                {
                    if (!string.IsNullOrEmpty(sensitiveValue))
                    {
                        value = value.Replace(
                            sensitiveValue,
                            "[REDACTED]",
                            StringComparison.Ordinal);
                    }
                }

                return SanitizeMetadata(value);
            }
        }

        return null;
    }

    private static string? SanitizeMetadata(
        string? value,
        IReadOnlyCollection<string>? sensitiveValues = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (sensitiveValues is not null)
        {
            foreach (var sensitiveValue in sensitiveValues)
            {
                if (!string.IsNullOrEmpty(sensitiveValue))
                {
                    normalized = normalized.Replace(
                        sensitiveValue,
                        "[REDACTED]",
                        StringComparison.Ordinal);
                }
            }
        }

        var sanitized = new string(normalized
            .Where(character => !char.IsControl(character))
            .Take(MaximumMetadataLength)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }

    private static string Sanitize(string value, IEnumerable<string> sensitiveValues)
    {
        var sanitized = value;
        foreach (var sensitiveValue in sensitiveValues)
        {
            if (!string.IsNullOrEmpty(sensitiveValue))
            {
                sanitized = sanitized.Replace(
                    sensitiveValue,
                    "[REDACTED]",
                    StringComparison.Ordinal);
            }
        }

        sanitized = SensitiveHeaderRegex().Replace(
            sanitized,
            "$1: [REDACTED]");
        sanitized = SensitiveHeaderNameRegex().Replace(
            sanitized,
            "[REDACTED]");
        sanitized = UrlRegex().Replace(
            sanitized,
            static match => SanitizeUrlText(match.Value));
        sanitized = string.Join(
            ' ',
            sanitized.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return sanitized.Length <= MaximumSafeMessageLength
            ? sanitized
            : sanitized[..MaximumSafeMessageLength];
    }

    private static string SanitizeUrlText(string value)
    {
        if (!Uri.TryCreate(value.TrimEnd('.', ',', ';', ')', ']', '}'), UriKind.Absolute, out var uri))
        {
            return "[REDACTED-URL]";
        }

        return SanitizeLocation(uri);
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

    [GeneratedRegex(@"(?im)\b(authorization|api-key|x-api-key)\s*:\s*[^\r\n]+")]
    private static partial Regex SensitiveHeaderRegex();

    [GeneratedRegex(@"(?i)\b(?:authorization|api-key|x-api-key)\b")]
    private static partial Regex SensitiveHeaderNameRegex();

    [GeneratedRegex("https?://[^\\s\"'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    private readonly record struct ErrorBody(string Value, bool Truncated);

    private readonly record struct ParsedError(string? Message, string? Type, string? Code);
}
