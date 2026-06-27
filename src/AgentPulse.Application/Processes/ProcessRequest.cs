namespace AgentPulse.Application.Processes;

public sealed record ProcessRequest
{
    public ProcessRequest(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (timeout <= TimeSpan.Zero && timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "Process timeout must be positive or infinite.");
        }

        Executable = executable;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        Timeout = timeout;
    }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string WorkingDirectory { get; }

    public TimeSpan Timeout { get; }
}
