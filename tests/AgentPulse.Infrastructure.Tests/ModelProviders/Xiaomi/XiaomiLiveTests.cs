using AgentPulse.Application.ChatModels;
using AgentPulse.Infrastructure.Credentials;
using AgentPulse.Infrastructure.ModelProviders.Xiaomi;

namespace AgentPulse.Infrastructure.Tests.ModelProviders.Xiaomi;

public sealed class XiaomiLiveTests
{
    [LiveXiaomiFact]
    [Trait("Category", "LiveXiaomi")]
    public async Task Real_api_returns_text_and_completion()
    {
        var credential = Environment.GetEnvironmentVariable("MIMO_API_KEY")!.Trim();
        var session = new EnvironmentCredentialSession(credential);
        var client = new XiaomiChatModelClient(
            new SingleHttpClientFactory(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }),
            new XiaomiModelOptions
            {
                MaxCompletionTokens = 16,
                FirstByteTimeout = TimeSpan.FromSeconds(30),
                StreamIdleTimeout = TimeSpan.FromMinutes(1),
            },
            new XiaomiSseParser(),
            session);
        var request = new ChatModelRequest(
        [
            new ChatModelMessage(ChatModelRole.System, "Answer briefly."),
            new ChatModelMessage(ChatModelRole.User, "Reply with one word: hello"),
        ]);
        var textDeltaSeen = false;
        ModelStreamEvent.Completed? completion = null;

        await foreach (var streamEvent in client.StreamAsync(request, CancellationToken.None))
        {
            if (streamEvent is ModelStreamEvent.TextDelta)
            {
                textDeltaSeen = true;
            }
            else if (streamEvent is ModelStreamEvent.Completed completed)
            {
                completion = completed;
            }
        }

        Assert.True(textDeltaSeen);
        Assert.NotNull(completion);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("secret", null, false)]
    [InlineData(null, "1", false)]
    [InlineData("secret", "1", true)]
    [InlineData("secret", "true", false)]
    [InlineData("secret", "yes", false)]
    [InlineData("secret", "0", false)]
    [InlineData("   ", "1", false)]
    public void Live_test_gate_requires_api_key_and_exact_opt_in_flag(
        string? apiKey,
        string? flag,
        bool expected)
    {
        Assert.Equal(expected, LiveXiaomiTestGate.IsEnabled(apiKey, flag));
    }

    private sealed class LiveXiaomiFactAttribute : FactAttribute
    {
        public LiveXiaomiFactAttribute()
        {
            var apiKey = Environment.GetEnvironmentVariable("MIMO_API_KEY");
            var optInFlag = Environment.GetEnvironmentVariable("AGENTPULSE_RUN_LIVE_TESTS");

            if (!LiveXiaomiTestGate.IsEnabled(apiKey, optInFlag))
            {
                Skip = "Set MIMO_API_KEY and AGENTPULSE_RUN_LIVE_TESTS=1 to run the optional Xiaomi MiMo live test.";
            }
        }
    }

    private static class LiveXiaomiTestGate
    {
        public static bool IsEnabled(string? apiKey, string? optInFlag)
        {
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   string.Equals(optInFlag, "1", StringComparison.Ordinal);
        }
    }

    private sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class EnvironmentCredentialSession(string credential)
        : IProviderCredentialSession
    {
        public void Set(string value, ProviderCredentialSource source) =>
            throw new NotSupportedException();

        public string GetRequiredCredential() => credential;

        public Task MarkAcceptedAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task MarkAuthenticationRejectedAsync(
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
