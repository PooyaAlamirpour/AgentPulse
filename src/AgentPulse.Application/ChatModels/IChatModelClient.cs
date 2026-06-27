namespace AgentPulse.Application.ChatModels;

public interface IChatModelClient
{
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ChatModelRequest request,
        CancellationToken cancellationToken);
}
