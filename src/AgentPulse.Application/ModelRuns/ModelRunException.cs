using AgentPulse.Application.ChatModels;

namespace AgentPulse.Application.ModelRuns;

public sealed class ModelRunException : Exception
{
    public ModelRunException(ModelRunErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public ModelRunException(
        ModelRunErrorCode code,
        string message,
        ModelProviderErrorCode providerErrorCode)
        : base(message)
    {
        if (!Enum.IsDefined(providerErrorCode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerErrorCode),
                providerErrorCode,
                "Unknown model provider error code.");
        }

        Code = code;
        ProviderErrorCode = providerErrorCode;
    }

    public ModelRunException(ModelRunErrorCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public ModelRunErrorCode Code { get; }

    public ModelProviderErrorCode? ProviderErrorCode { get; }
}
