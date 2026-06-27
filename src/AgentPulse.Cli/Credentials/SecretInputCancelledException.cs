namespace AgentPulse.Cli.Credentials;

public sealed class SecretInputCancelledException : OperationCanceledException
{
    public SecretInputCancelledException()
        : base("Secret input was cancelled by the user.")
    {
    }
}
