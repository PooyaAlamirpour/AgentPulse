using System.Runtime.CompilerServices;
using AgentPulse.Application.ChatModels;
using AgentPulse.Infrastructure.Credentials;
using AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

namespace AgentPulse.Infrastructure.ModelProviders.Xiaomi;

public sealed class XiaomiChatModelClient : IChatModelClient
{
    public const string HttpClientName = OpenAiCompatibleChatModelClient.HttpClientName;

    private readonly OpenAiCompatibleChatModelClient _inner;

    public XiaomiChatModelClient(
        IHttpClientFactory httpClientFactory,
        XiaomiModelOptions options,
        XiaomiSseParser sseParser,
        IProviderCredentialSession credentialSession)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sseParser);
        _inner = new OpenAiCompatibleChatModelClient(
            httpClientFactory,
            options.ToOpenAiCompatibleOptions(),
            sseParser.Inner,
            credentialSession);
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ChatModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var streamEvent in _inner
                           .StreamAsync(request, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            yield return streamEvent;
        }
    }
}
