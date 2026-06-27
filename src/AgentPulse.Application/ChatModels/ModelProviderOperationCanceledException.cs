namespace AgentPulse.Application.ChatModels;

public sealed class ModelProviderOperationCanceledException : OperationCanceledException
{
    public ModelProviderOperationCanceledException(
        ModelFailureStage failureStage,
        CancellationToken cancellationToken,
        Exception? innerException = null)
        : base(
            "The model request was cancelled.",
            innerException,
            cancellationToken)
    {
        if (!Enum.IsDefined(failureStage))
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureStage),
                failureStage,
                "Unknown model failure stage.");
        }

        FailureStage = failureStage;
    }

    public ModelProviderErrorCode Code => ModelProviderErrorCode.Cancelled;

    public ModelFailureStage FailureStage { get; }
}
