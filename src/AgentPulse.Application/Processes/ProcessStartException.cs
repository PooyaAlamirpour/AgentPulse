namespace AgentPulse.Application.Processes;

public sealed class ProcessStartException : Exception
{
    public ProcessStartException(string executable, Exception innerException)
        : base($"The executable '{executable}' could not be started.", innerException)
    {
        Executable = executable;
    }

    public string Executable { get; }
}
