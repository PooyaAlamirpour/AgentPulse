namespace AgentPulse.Application.ChatModels;

public interface IChatModelClient
{
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ChatModelRequest request,
        CancellationToken cancellationToken);

    Task<ChatModelResponse> CompleteAsync(
        ChatModelRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromException<ChatModelResponse>(new ModelProviderException(
            ModelProviderErrorCode.UnsupportedFeature,
            "The configured model client does not support non-streaming tool calling."));
    }
}
