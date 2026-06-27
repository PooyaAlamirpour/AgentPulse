namespace AgentPulse.Application.ModelRuns;

public interface IModelOutputSink
{
    Task WriteDeltaAsync(string delta, CancellationToken cancellationToken = default);

    Task CompleteAsync(CancellationToken cancellationToken = default);
}
