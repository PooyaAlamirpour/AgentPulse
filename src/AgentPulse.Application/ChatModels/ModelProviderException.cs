using System.Net;

namespace AgentPulse.Application.ChatModels;

public sealed class ModelProviderException : Exception
{
    public ModelProviderException(ModelProviderErrorCode code, string message)
        : this(code, message, ModelFailureStage.BeforeFirstToken)
    {
    }

    public ModelProviderException(
        ModelProviderErrorCode code,
        string message,
        Exception innerException)
        : this(code, message, ModelFailureStage.BeforeFirstToken, innerException)
    {
    }

    public ModelProviderException(
        ModelProviderErrorCode code,
        string message,
        ModelFailureStage failureStage,
        Exception? innerException = null,
        HttpStatusCode? httpStatusCode = null,
        string? providerErrorCode = null,
        string? providerErrorType = null,
        TimeSpan? retryAfter = null,
        string? requestId = null)
        : base(message, innerException)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown model provider error code.");
        }

        if (!Enum.IsDefined(failureStage))
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureStage),
                failureStage,
                "Unknown model failure stage.");
        }

        Code = code;
        FailureStage = failureStage;
        HttpStatusCode = httpStatusCode;
        ProviderErrorCode = Normalize(providerErrorCode);
        ProviderErrorType = Normalize(providerErrorType);
        RetryAfter = retryAfter;
        RequestId = Normalize(requestId);
    }

    public ModelProviderErrorCode Code { get; }

    public ModelFailureStage FailureStage { get; }

    public HttpStatusCode? HttpStatusCode { get; }

    public string? ProviderErrorCode { get; }

    public string? ProviderErrorType { get; }

    public TimeSpan? RetryAfter { get; }

    public string? RequestId { get; }

    public ModelProviderException WithFailureStage(ModelFailureStage failureStage)
    {
        return FailureStage == failureStage
            ? this
            : new ModelProviderException(
                Code,
                Message,
                failureStage,
                this,
                HttpStatusCode,
                ProviderErrorCode,
                ProviderErrorType,
                RetryAfter,
                RequestId);
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
