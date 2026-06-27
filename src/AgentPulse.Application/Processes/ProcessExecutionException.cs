namespace AgentPulse.Application.Processes;

public sealed class ProcessExecutionException : Exception
{
    public ProcessExecutionException(string executable, Exception innerException)
        : base($"The executable '{executable}' failed during execution.", innerException)
    {
        Executable = executable;
    }

    public string Executable { get; }
}
