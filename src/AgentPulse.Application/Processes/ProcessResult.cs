namespace AgentPulse.Application.Processes;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
