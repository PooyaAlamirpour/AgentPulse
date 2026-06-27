namespace AgentPulse.Application.ModelRuns;

public interface IRunPrompt
{
    Task<RunPromptResult> ExecuteAsync(
        RunPromptRequest request,
        CancellationToken cancellationToken = default);
}
