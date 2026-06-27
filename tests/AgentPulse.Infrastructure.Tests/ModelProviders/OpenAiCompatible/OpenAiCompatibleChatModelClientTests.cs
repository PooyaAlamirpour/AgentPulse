using System.Net;
using System.Text.Json;
using AgentPulse.Application.ChatModels;
using AgentPulse.Infrastructure.Credentials;
using AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

namespace AgentPulse.Infrastructure.Tests.ModelProviders.OpenAiCompatible;

public sealed class OpenAiCompatibleChatModelClientTests
{
    private const string Secret = "provider-test-secret-never-log";
    private const string SystemPrompt = "Private system prompt that must not leak";
    private const string UserPrompt = "Private user history that must not leak";

    [Fact]
    public async Task Xiaomi_profile_uses_api_key_header_and_includes_thinking_configuration()
    {
        await using var server = SuccessfulServer();
        var credential = new RecordingCredentialSession(Secret);
        var options = CreateOptions(server.BaseUri);
        var client = CreateClient(options, credential);

        var events = await ReadAllAsync(client);
        var request = await server.Request;

        Assert.Equal("POST /v1/chat/completions HTTP/1.1", request.RequestLine);
        Assert.Equal(Secret, request.Headers["api-key"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.DoesNotContain(Secret, request.Body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Assert.Equal("mimo-v2.5-pro", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal(4096, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.False(root.TryGetProperty("functions", out _));
        Assert.Equal(["Hel", "lo"], TextDeltas(events));
        Assert.Equal(1, credential.AcceptedCalls);
        Assert.Equal(0, credential.RejectedCalls);
    }

    [Fact]
    public async Task Generic_profile_uses_bearer_authentication_and_omits_thinking_configuration()
    {
        await using var server = SuccessfulServer();
        var options = CreateOptions(server.BaseUri);
        options.Model = "generic-model";
        options.ChatCompletionsPath = "/custom//chat/completions/";
        options.AuthenticationMode = OpenAiCompatibleAuthenticationMode.Bearer;
        options.ApiKeyHeaderName = "Host";
        options.ApiKeyEnvironmentVariable = "GENERIC_API_KEY";
        options.IncludeThinkingConfiguration = false;
        var client = CreateClient(options, new RecordingCredentialSession(Secret));

        await ReadAllAsync(client);
        var request = await server.Request;

        Assert.Equal("POST /v1/custom/chat/completions HTTP/1.1", request.RequestLine);
        Assert.Equal($"Bearer {Secret}", request.Headers["Authorization"]);
        Assert.False(request.Headers.ContainsKey("api-key"));
        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Assert.Equal("generic-model", root.GetProperty("model").GetString());
        Assert.False(root.TryGetProperty("thinking", out _));
        var messages = root.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(["system", "user", "assistant"], messages.Select(value => value.GetProperty("role").GetString()));
    }

    [Theory]
    [InlineData(400, ModelProviderErrorCode.InvalidRequest)]
    [InlineData(401, ModelProviderErrorCode.Authentication)]
    [InlineData(403, ModelProviderErrorCode.PermissionDenied)]
    [InlineData(408, ModelProviderErrorCode.Timeout)]
    [InlineData(409, ModelProviderErrorCode.InvalidRequest)]
    [InlineData(429, ModelProviderErrorCode.RateLimited)]
    [InlineData(500, ModelProviderErrorCode.Unavailable)]
    [InlineData(502, ModelProviderErrorCode.Unavailable)]
    [InlineData(503, ModelProviderErrorCode.Unavailable)]
    public async Task Maps_http_status_to_provider_independent_error_taxonomy(
        int statusCode,
        ModelProviderErrorCode expectedCode)
    {
        await using var server = ErrorServer(
            statusCode,
            "{\"error\":{\"message\":\"provider rejected request\",\"type\":\"request_error\",\"code\":\"provider_code\"}}");
        var credential = new RecordingCredentialSession(Secret);
        var client = CreateClient(CreateOptions(server.BaseUri), credential);

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
        Assert.Equal((HttpStatusCode)statusCode, exception.HttpStatusCode);
        Assert.Equal("provider_code", exception.ProviderErrorCode);
        Assert.Equal("request_error", exception.ProviderErrorType);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SystemPrompt, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(UserPrompt, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(statusCode is 401 or 403 ? 1 : 0, credential.RejectedCalls);
    }

    [Theory]
    [InlineData("{\"error\":{\"message\":\"wrapped\",\"type\":\"wrapped-type\",\"code\":\"wrapped-code\"}}", "wrapped", "wrapped-type", "wrapped-code")]
    [InlineData("{\"message\":\"unwrapped\",\"type\":\"plain-type\",\"code\":42}", "unwrapped", "plain-type", "42")]
    [InlineData("plain text failure", "plain text failure", null, null)]
    [InlineData("", null, null, null)]
    [InlineData("{broken", "{broken", null, null)]
    public async Task Parses_standard_and_non_standard_error_bodies(
        string body,
        string? expectedMessage,
        string? expectedType,
        string? expectedCode)
    {
        await using var server = ErrorServer(400, body);
        var client = CreateClient(CreateOptions(server.BaseUri), new RecordingCredentialSession(Secret));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        if (expectedMessage is null)
        {
            Assert.Equal("The model endpoint returned HTTP 400.", exception.Message);
        }
        else
        {
            Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(expectedType, exception.ProviderErrorType);
        Assert.Equal(expectedCode, exception.ProviderErrorCode);
    }

    [Fact]
    public async Task Error_metadata_and_sensitive_values_are_sanitized()
    {
        var body = "{\"error\":{\"message\":\"" +
                   Secret + " " + SystemPrompt + " " + UserPrompt +
                   " Authorization: Bearer " + Secret +
                   " https://provider.example/fail?api_key=" + Secret +
                   "\",\"type\":\"type-" + Secret +
                   "\",\"code\":\"code-" + Secret + "\"}}";
        var headers = new Dictionary<string, string>
        {
            ["Retry-After"] = "9",
            ["x-request-id"] = "request-" + Secret,
            ["Content-Type"] = "text/plain",
        };
        await using var server = ErrorServer(429, body, headers);
        var client = CreateClient(CreateOptions(server.BaseUri), new RecordingCredentialSession(Secret));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));
        var rendered = exception.ToString();

        Assert.Equal(TimeSpan.FromSeconds(9), exception.RetryAfter);
        Assert.Equal("request-[REDACTED]", exception.RequestId);
        Assert.Equal("type-[REDACTED]", exception.ProviderErrorType);
        Assert.Equal("code-[REDACTED]", exception.ProviderErrorCode);
        Assert.DoesNotContain("api_key=", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(SystemPrompt, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(UserPrompt, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_error_body_is_bounded()
    {
        await using var server = ErrorServer(500, new string('x', 20 * 1024));
        var client = CreateClient(CreateOptions(server.BaseUri), new RecordingCredentialSession(Secret));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Contains("truncated", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(exception.Message.Length < 2300);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Redirects_are_not_followed_and_target_receives_no_credential(int statusCode)
    {
        await using var target = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteResponseAsync(
                stream,
                500,
                "Unexpected",
                "redirect target must not be called",
                null,
                cancellationToken);
        });
        var sensitiveLocation = new Uri(target.BaseUri, "redirect?api_key=" + Secret);
        await using var source = ErrorServer(
            statusCode,
            string.Empty,
            new Dictionary<string, string> { ["Location"] = sensitiveLocation.ToString() });
        var client = CreateClient(CreateOptions(source.BaseUri), new RecordingCredentialSession(Secret));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal(ModelProviderErrorCode.InvalidResponse, exception.Code);
        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("api_key=", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(target.HasReceivedRequest);
    }

    [Fact]
    public async Task Invalid_stream_before_first_delta_reports_before_first_token()
    {
        await using var server = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteSseHeadersAsync(stream, cancellationToken);
            await LocalModelServer.WriteAsync(stream, "data: {broken}\n\n", cancellationToken);
        });
        var client = CreateClient(CreateOptions(server.BaseUri), new RecordingCredentialSession(Secret));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
        Assert.Equal(ModelProviderErrorCode.InvalidResponse, exception.Code);
    }

    [Fact]
    public async Task Invalid_stream_after_delta_reports_after_first_token_and_preserves_delta()
    {
        await using var server = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteSseHeadersAsync(stream, cancellationToken);
            await LocalModelServer.WriteAsync(stream, Data("Hel") + "data: {broken}\n\n", cancellationToken);
        });
        var client = CreateClient(CreateOptions(server.BaseUri), new RecordingCredentialSession(Secret));
        var deltas = new List<string>();

        var exception = await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var streamEvent in client.StreamAsync(CreateRequest(), CancellationToken.None))
            {
                if (streamEvent is ModelStreamEvent.TextDelta delta)
                {
                    deltas.Add(delta.Text);
                }
            }
        });

        Assert.Equal(["Hel"], deltas);
        Assert.Equal(ModelFailureStage.AfterFirstToken, exception.FailureStage);
        Assert.Equal(ModelProviderErrorCode.InvalidResponse, exception.Code);
    }

    [Fact]
    public async Task First_byte_timeout_reports_before_first_token()
    {
        await using var server = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteSseHeadersAsync(stream, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var options = CreateOptions(server.BaseUri);
        options.FirstByteTimeout = TimeSpan.FromMilliseconds(250);
        var client = CreateClient(options, new RecordingCredentialSession(Secret));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => ReadAllAsync(client));

        Assert.Equal(ModelProviderErrorCode.Timeout, exception.Code);
        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
    }

    [Fact]
    public async Task Idle_timeout_after_delta_reports_after_first_token()
    {
        await using var server = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteSseHeadersAsync(stream, cancellationToken);
            await LocalModelServer.WriteAsync(stream, Data("Hel"), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var options = CreateOptions(server.BaseUri);
        options.StreamIdleTimeout = TimeSpan.FromMilliseconds(250);
        var client = CreateClient(options, new RecordingCredentialSession(Secret));
        var deltas = new List<string>();

        var exception = await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var streamEvent in client.StreamAsync(CreateRequest(), CancellationToken.None))
            {
                if (streamEvent is ModelStreamEvent.TextDelta delta)
                {
                    deltas.Add(delta.Text);
                }
            }
        });

        Assert.Equal(["Hel"], deltas);
        Assert.Equal(ModelProviderErrorCode.Timeout, exception.Code);
        Assert.Equal(ModelFailureStage.AfterFirstToken, exception.FailureStage);
    }

    [Fact]
    public async Task Cancellation_before_token_cancels_network_request_and_reports_stage()
    {
        var clientDisconnected = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteSseHeadersAsync(stream, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            try
            {
                var buffer = new byte[1];
                var read = await stream.ReadAsync(buffer, cancellationToken);
                clientDisconnected.TrySetResult(read == 0);
            }
            catch (IOException)
            {
                clientDisconnected.TrySetResult(true);
            }
        });
        var client = CreateClient(CreateOptions(server.BaseUri), new RecordingCredentialSession(Secret));
        using var cancellation = new CancellationTokenSource();
        var run = ReadAllAsync(client, cancellation.Token);
        await server.Request.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<ModelProviderOperationCanceledException>(() => run);

        Assert.Equal(ModelProviderErrorCode.Cancelled, exception.Code);
        Assert.Equal(ModelFailureStage.BeforeFirstToken, exception.FailureStage);
        Assert.True(await clientDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Cancellation_after_delta_reports_stage_and_preserves_delta()
    {
        await using var server = new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteSseHeadersAsync(stream, cancellationToken);
            await LocalModelServer.WriteAsync(stream, Data("Hel"), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var client = CreateClient(CreateOptions(server.BaseUri), new RecordingCredentialSession(Secret));
        using var cancellation = new CancellationTokenSource();
        var deltas = new List<string>();

        var exception = await Assert.ThrowsAsync<ModelProviderOperationCanceledException>(async () =>
        {
            await foreach (var streamEvent in client.StreamAsync(CreateRequest(), cancellation.Token))
            {
                if (streamEvent is ModelStreamEvent.TextDelta delta)
                {
                    deltas.Add(delta.Text);
                    cancellation.Cancel();
                }
            }
        });

        Assert.Equal(["Hel"], deltas);
        Assert.Equal(ModelProviderErrorCode.Cancelled, exception.Code);
        Assert.Equal(ModelFailureStage.AfterFirstToken, exception.FailureStage);
    }

    private static LocalModelServer SuccessfulServer()
    {
        return new LocalModelServer(async (stream, request, cancellationToken) =>
        {
            await LocalModelServer.WriteSseHeadersAsync(stream, cancellationToken);
            await LocalModelServer.WriteAsync(
                stream,
                Data("Hel") + Data("lo") + Finish("stop") + Usage(2, 3, 5) + Done(),
                cancellationToken);
        });
    }

    private static LocalModelServer ErrorServer(
        int statusCode,
        string body,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        return new LocalModelServer((stream, request, cancellationToken) =>
            LocalModelServer.WriteResponseAsync(
                stream,
                statusCode,
                Reason(statusCode),
                body,
                headers,
                cancellationToken));
    }

    private static OpenAiCompatibleChatModelClient CreateClient(
        OpenAiCompatibleModelOptions options,
        IProviderCredentialSession credentialSession)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return new OpenAiCompatibleChatModelClient(
            new SingleHttpClientFactory(httpClient),
            options,
            new OpenAiCompatibleSseParser(),
            credentialSession);
    }

    private static OpenAiCompatibleModelOptions CreateOptions(Uri baseUri)
    {
        return new OpenAiCompatibleModelOptions
        {
            BaseUrl = new Uri(baseUri, "v1").ToString(),
            FirstByteTimeout = TimeSpan.FromSeconds(2),
            StreamIdleTimeout = TimeSpan.FromSeconds(2),
        };
    }

    private static ChatModelRequest CreateRequest()
    {
        return new ChatModelRequest(
        [
            new ChatModelMessage(ChatModelRole.System, SystemPrompt),
            new ChatModelMessage(ChatModelRole.User, UserPrompt),
            new ChatModelMessage(ChatModelRole.Assistant, "Prior assistant response"),
        ]);
    }

    private static async Task<IReadOnlyList<ModelStreamEvent>> ReadAllAsync(
        OpenAiCompatibleChatModelClient client,
        CancellationToken cancellationToken = default)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var streamEvent in client.StreamAsync(CreateRequest(), cancellationToken))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    private static string[] TextDeltas(IEnumerable<ModelStreamEvent> events)
    {
        return events.OfType<ModelStreamEvent.TextDelta>()
            .Select(value => value.Text)
            .ToArray();
    }

    private static string Data(string text) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"{text}\"}},\"finish_reason\":null}}]}}\n\n";

    private static string Finish(string reason) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"{reason}\"}}]}}\n\n";

    private static string Usage(long prompt, long completion, long total) =>
        $"data: {{\"choices\":[],\"usage\":{{\"prompt_tokens\":{prompt},\"completion_tokens\":{completion},\"total_tokens\":{total}}}}}\n\n";

    private static string Done() => "data: [DONE]\n\n";

    private static string Reason(int statusCode)
    {
        return statusCode switch
        {
            301 => "Moved Permanently",
            302 => "Found",
            303 => "See Other",
            307 => "Temporary Redirect",
            308 => "Permanent Redirect",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            408 => "Request Timeout",
            409 => "Conflict",
            429 => "Too Many Requests",
            500 => "Internal Server Error",
            502 => "Bad Gateway",
            503 => "Service Unavailable",
            _ => "Error",
        };
    }

    private sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(OpenAiCompatibleChatModelClient.HttpClientName, name);
            return client;
        }
    }

    private sealed class RecordingCredentialSession(string credential)
        : IProviderCredentialSession
    {
        public int AcceptedCalls { get; private set; }

        public int RejectedCalls { get; private set; }

        public void Set(string value, ProviderCredentialSource source) =>
            throw new NotSupportedException();

        public string GetRequiredCredential() => credential;

        public Task MarkAcceptedAsync(CancellationToken cancellationToken = default)
        {
            AcceptedCalls++;
            return Task.CompletedTask;
        }

        public Task MarkAuthenticationRejectedAsync(
            CancellationToken cancellationToken = default)
        {
            RejectedCalls++;
            return Task.CompletedTask;
        }
    }
}
