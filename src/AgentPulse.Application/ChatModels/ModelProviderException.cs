namespace AgentPulse.Application.ChatModels;

public sealed class ModelProviderException : Exception
{
    public ModelProviderException(ModelProviderErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public ModelProviderException(
        ModelProviderErrorCode code,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public ModelProviderErrorCode Code { get; }
}
