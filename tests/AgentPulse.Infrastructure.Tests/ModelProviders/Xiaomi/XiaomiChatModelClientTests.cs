using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentPulse.Application.ChatModels;
using AgentPulse.Infrastructure.Credentials;
using AgentPulse.Infrastructure.ModelProviders.Xiaomi;

namespace AgentPulse.Infrastructure.Tests.ModelProviders.Xiaomi;

public sealed class XiaomiChatModelClientTests
{
    private const string Secret = "mimo-test-secret-never-log";

    [Fact]
    public async Task Sends_openai_compatible_request_and_streams_separate_deltas()
    {
        await using var server = new LocalHttpServer(async (stream, request, cancellationToken) =>
        {
            await WriteStreamingHeadersAsync(stream, cancellationToken);
            await WriteAsync(stream, Data("Hel"), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            await Task.Yield();
            await WriteAsync(
                stream,
                Data("lo") + Finish("stop") + Usage(2, 3, 5) + Done(),
                cancellationToken);
        });
        var credential = new RecordingCredentialSession(Secret);
        var client = CreateClient(server.BaseUri, credential);

        var events = await ReadAllAsync(client);
        var request = await server.Request;

        Assert.Equal(Secret, request.Headers["api-key"]);
        Assert.DoesNotContain(Secret, request.Body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(request.Body);
        var root = json.RootElement;
        Assert.Equal("mimo-v2.5-pro", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(4096, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.Equal(
            ["Hel", "lo"],
            events.OfType<ModelStreamEvent.TextDelta>().Select(value => value.Text));
        Assert.Equal(
            ModelFinishReason.Stop,
            Assert.IsType<ModelStreamEvent.Completed>(events[^1]).FinishReason);
        var usage = Assert.Single(events.OfType<ModelStreamEvent.Usage>()).Value;
        Assert.Equal(new ModelUsage(2, 3, 5), usage);
        Assert.Equal(1, credential.AcceptedCalls);
        Assert.Equal(0, credential.RejectedCalls);
    }


    [Fact]
    public async Task Public_http_base_url_is_rejected_before_http_client_or_credential_use()
    {
        var factory = new RecordingHttpClientFactory();
        var credential = new RecordingCredentialSession(Secret);
        var client = new XiaomiChatModelClient(
            factory,
            new XiaomiModelOptions { BaseUrl = "http://example.com/v1" },
            new XiaomiSseParser(),
            credential);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAllAsync(client));

        Assert.Equal(0, factory.CreateClientCalls);
        Assert.Equal(0, credential.AcceptedCalls);
        Assert.Equal(0, credential.RejectedCalls);
    }

    [Theory]
    [InlineData(401, ModelProviderErrorCode.Authentication)]
    [InlineData(403, ModelProviderErrorCode.PermissionDenied)]
    public async Task Authentication_failure_rejects_credential_without_leaking_secret(
        int statusCode,
        ModelProviderErrorCode expectedCode)
    {
        await using var server = new LocalHttpServer(async (stream, request, cancellationToken) =>
        {
            await WriteResponseAsync(
                stream,
                statusCode,
                $"credential {Secret} api-key authorization rejected",
                cancellationToken);
        });
        var credential = new RecordingCredentialSession(Secret);
        var client = CreateClient(server.BaseUri, credential);

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() =>
            ReadAllAsync(client));

        Assert.Equal(expectedCode, exception.Code);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("api-key", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, credential.RejectedCalls);
        Assert.Equal(0, credential.AcceptedCalls);
    }

    [Theory]
    [InlineData(429, ModelProviderErrorCode.RateLimited)]
    [InlineData(500, ModelProviderErrorCode.ServiceUnavailable)]
    public async Task Maps_http_failures(int statusCode, ModelProviderErrorCode expectedCode)
    {
        await using var server = new LocalHttpServer((stream, request, cancellationToken) =>
            WriteResponseAsync(stream, statusCode, "provider unavailable", cancellationToken));
        var client = CreateClient(server.BaseUri, new RecordingCredentialSession(Secret));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() =>
            ReadAllAsync(client));

        Assert.Equal(expectedCode, exception.Code);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connection_cut_before_first_delta_is_an_incomplete_stream_failure()
    {
        await using var server = new LocalHttpServer(async (stream, request, cancellationToken) =>
        {
            await WriteStreamingHeadersAsync(stream, cancellationToken);
        });
        var client = CreateClient(server.BaseUri, new RecordingCredentialSession(Secret));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() =>
            ReadAllAsync(client));

        Assert.Equal(ModelProviderErrorCode.InvalidResponse, exception.Code);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connection_cut_after_partial_delta_is_an_incomplete_stream_failure()
    {
        await using var server = new LocalHttpServer(async (stream, request, cancellationToken) =>
        {
            await WriteStreamingHeadersAsync(stream, cancellationToken);
            await WriteAsync(stream, Data("Hel"), cancellationToken);
        });
        var client = CreateClient(server.BaseUri, new RecordingCredentialSession(Secret));
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
        Assert.Equal(ModelProviderErrorCode.InvalidResponse, exception.Code);
    }

    [Fact]
    public async Task Invalid_json_after_partial_delta_preserves_already_emitted_delta()
    {
        await using var server = new LocalHttpServer(async (stream, request, cancellationToken) =>
        {
            await WriteStreamingHeadersAsync(stream, cancellationToken);
            await WriteAsync(stream, Data("Hel") + "data: {broken}\n\n", cancellationToken);
        });
        var client = CreateClient(server.BaseUri, new RecordingCredentialSession(Secret));
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
        Assert.Equal(ModelProviderErrorCode.InvalidResponse, exception.Code);
    }

    [Fact]
    public async Task First_byte_timeout_is_distinct_from_user_cancellation()
    {
        await using var server = new LocalHttpServer(async (stream, request, cancellationToken) =>
        {
            await WriteStreamingHeadersAsync(stream, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        });
        var client = CreateClient(
            server.BaseUri,
            new RecordingCredentialSession(Secret),
            firstByteTimeout: TimeSpan.FromMilliseconds(300));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() =>
            ReadAllAsync(client));

        Assert.Equal(ModelProviderErrorCode.FirstByteTimeout, exception.Code);
    }

    [Fact]
    public async Task Idle_timeout_after_partial_text_is_reported_separately()
    {
        await using var server = new LocalHttpServer(async (stream, request, cancellationToken) =>
        {
            await WriteStreamingHeadersAsync(stream, cancellationToken);
            await WriteAsync(stream, Data("Hel"), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        });
        var client = CreateClient(
            server.BaseUri,
            new RecordingCredentialSession(Secret),
            idleTimeout: TimeSpan.FromMilliseconds(300));
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
        Assert.Equal(ModelProviderErrorCode.StreamIdleTimeout, exception.Code);
    }

    [Fact]
    public async Task User_cancellation_after_partial_delta_preserves_emitted_text()
    {
        var firstDeltaSent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalHttpServer(async (stream, request, cancellationToken) =>
        {
            await WriteStreamingHeadersAsync(stream, cancellationToken);
            await WriteAsync(stream, Data("Hel"), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            firstDeltaSent.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        });
        var client = CreateClient(
            server.BaseUri,
            new RecordingCredentialSession(Secret),
            idleTimeout: TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var deltas = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
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

        await firstDeltaSent.Task;
        Assert.Equal(["Hel"], deltas);
    }

    [Fact]
    public async Task User_cancellation_is_not_converted_to_timeout()
    {
        await using var server = new LocalHttpServer(async (stream, request, cancellationToken) =>
        {
            await WriteStreamingHeadersAsync(stream, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        });
        var client = CreateClient(
            server.BaseUri,
            new RecordingCredentialSession(Secret),
            firstByteTimeout: TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var readTask = ReadAllAsync(client, cancellation.Token);
        await server.Request.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
    }

    private static XiaomiChatModelClient CreateClient(
        Uri baseUri,
        IProviderCredentialSession credentialSession,
        TimeSpan? firstByteTimeout = null,
        TimeSpan? idleTimeout = null)
    {
        var options = new XiaomiModelOptions
        {
            BaseUrl = new Uri(baseUri, "v1").ToString(),
            FirstByteTimeout = firstByteTimeout ?? TimeSpan.FromSeconds(2),
            StreamIdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(2),
        };
        return new XiaomiChatModelClient(
            new SingleHttpClientFactory(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }),
            options,
            new XiaomiSseParser(),
            credentialSession);
    }

    private static ChatModelRequest CreateRequest()
    {
        return new ChatModelRequest(
        [
            new ChatModelMessage(ChatModelRole.System, "System context"),
            new ChatModelMessage(ChatModelRole.User, "Reply with exactly: Hello"),
        ]);
    }

    private static async Task<IReadOnlyList<ModelStreamEvent>> ReadAllAsync(
        XiaomiChatModelClient client,
        CancellationToken cancellationToken = default)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var streamEvent in client.StreamAsync(CreateRequest(), cancellationToken))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    private static Task WriteStreamingHeadersAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        return WriteAsync(
            stream,
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/event-stream; charset=utf-8\r\n" +
            "Cache-Control: no-cache\r\n" +
            "Connection: close\r\n\r\n",
            cancellationToken);
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        int statusCode,
        string body,
        CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var reason = statusCode switch
        {
            401 => "Unauthorized",
            403 => "Forbidden",
            429 => "Too Many Requests",
            500 => "Internal Server Error",
            _ => "Error",
        };
        await WriteAsync(
            stream,
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n",
            cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
    }

    private static Task WriteAsync(
        Stream stream,
        string value,
        CancellationToken cancellationToken)
    {
        return stream.WriteAsync(Encoding.UTF8.GetBytes(value), cancellationToken).AsTask();
    }

    private static string Data(string text) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"{text}\"}},\"finish_reason\":null}}]}}\n\n";

    private static string Finish(string reason) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"{reason}\"}}]}}\n\n";

    private static string Usage(long prompt, long completion, long total) =>
        $"data: {{\"choices\":[],\"usage\":{{\"prompt_tokens\":{prompt},\"completion_tokens\":{completion},\"total_tokens\":{total}}}}}\n\n";

    private static string Done() => "data: [DONE]\n\n";


    private sealed class RecordingHttpClientFactory : IHttpClientFactory
    {
        public int CreateClientCalls { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateClientCalls++;
            throw new InvalidOperationException("HTTP client creation should not be reached.");
        }
    }

    private sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
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

    private sealed record CapturedRequest(
        string RequestLine,
        IReadOnlyDictionary<string, string> Headers,
        string Body);

    private sealed class LocalHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serverTask;
        private readonly TaskCompletionSource<CapturedRequest> _request =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LocalHttpServer(
            Func<NetworkStream, CapturedRequest, CancellationToken, Task> responseWriter)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            BaseUri = new Uri($"http://127.0.0.1:{endpoint.Port}/");
            _serverTask = RunAsync(responseWriter);
        }

        public Uri BaseUri { get; }
        public Task<CapturedRequest> Request => _request.Task;

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();

            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch (SocketException) when (_cancellation.IsCancellationRequested)
            {
            }

            _cancellation.Dispose();
        }

        private async Task RunAsync(
            Func<NetworkStream, CapturedRequest, CancellationToken, Task> responseWriter)
        {
            using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
            await using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, _cancellation.Token);
            _request.TrySetResult(request);
            await responseWriter(stream, request, _cancellation.Token);
            await stream.FlushAsync(_cancellation.Token);
        }

        private static async Task<CapturedRequest> ReadRequestAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var bytes = new List<byte>();
            var buffer = new byte[1024];
            var headerEnd = -1;

            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    throw new IOException("The client disconnected before sending request headers.");
                }

                bytes.AddRange(buffer.AsSpan(0, read).ToArray());
                headerEnd = FindHeaderEnd(bytes);
            }

            var headerText = Encoding.ASCII.GetString(bytes.Take(headerEnd).ToArray());
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var separator = line.IndexOf(':');
                if (separator > 0)
                {
                    headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }
            }

            var contentLength = headers.TryGetValue("Content-Length", out var lengthValue)
                ? int.Parse(lengthValue, System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            var bodyOffset = headerEnd + 4;
            while (bytes.Count - bodyOffset < contentLength)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    throw new IOException("The client disconnected before sending the request body.");
                }

                bytes.AddRange(buffer.AsSpan(0, read).ToArray());
            }

            var body = Encoding.UTF8.GetString(
                bytes.Skip(bodyOffset).Take(contentLength).ToArray());
            return new CapturedRequest(lines[0], headers, body);
        }

        private static int FindHeaderEnd(IReadOnlyList<byte> bytes)
        {
            for (var index = 0; index <= bytes.Count - 4; index++)
            {
                if (bytes[index] == '\r' &&
                    bytes[index + 1] == '\n' &&
                    bytes[index + 2] == '\r' &&
                    bytes[index + 3] == '\n')
                {
                    return index;
                }
            }

            return -1;
        }
    }
}
