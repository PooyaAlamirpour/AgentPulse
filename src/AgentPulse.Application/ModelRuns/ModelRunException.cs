namespace AgentPulse.Application.ModelRuns;

public sealed class ModelRunException : Exception
{
    public ModelRunException(ModelRunErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public ModelRunException(ModelRunErrorCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public ModelRunErrorCode Code { get; }
}
