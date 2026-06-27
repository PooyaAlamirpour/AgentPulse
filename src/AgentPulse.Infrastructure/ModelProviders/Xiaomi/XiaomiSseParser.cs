using System.Runtime.CompilerServices;
using AgentPulse.Application.ChatModels;
using AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

namespace AgentPulse.Infrastructure.ModelProviders.Xiaomi;

public sealed class XiaomiSseParser
{
    private readonly OpenAiCompatibleSseParser _inner = new();

    internal OpenAiCompatibleSseParser Inner => _inner;

    public async IAsyncEnumerable<ModelStreamEvent> ParseAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var streamEvent in _inner
                           .ParseAsync(stream, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            yield return streamEvent;
        }
    }
}
