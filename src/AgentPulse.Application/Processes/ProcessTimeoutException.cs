namespace AgentPulse.Application.Processes;

public sealed class ProcessTimeoutException : TimeoutException
{
    public ProcessTimeoutException(string executable, TimeSpan timeout)
        : base($"The executable '{executable}' exceeded the timeout of {timeout}.")
    {
        Executable = executable;
        Timeout = timeout;
    }

    public string Executable { get; }

    public TimeSpan Timeout { get; }
}
