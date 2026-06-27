namespace AgentPulse.Application.Processes;

public sealed class ProcessExecutableNotFoundException : Exception
{
    public ProcessExecutableNotFoundException(string executable, Exception innerException)
        : base($"The executable '{executable}' could not be found.", innerException)
    {
        Executable = executable;
    }

    public string Executable { get; }
}
