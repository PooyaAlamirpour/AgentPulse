using System.Net;
using System.Net.Sockets;
using System.Text;
using AgentPulse.Application.ChatModels;
using AgentPulse.Application.ModelRequests;
using AgentPulse.Application.ModelRuns;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Application.SessionRuns;
using AgentPulse.Domain.Messages;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;
using AgentPulse.Infrastructure.Credentials;
using AgentPulse.Infrastructure.ModelProviders.Xiaomi;
using AgentPulse.Infrastructure.Persistence;
using AgentPulse.Infrastructure.Persistence.Repositories;
using AgentPulse.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Tests.ModelRuns;

public sealed class StreamingRunEndToEndTests
{
    private static readonly DateTime UtcNow =
        new(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Real_vertical_flow_commits_messages_streams_and_finalizes_database_state()
    {
        const string secret = "local-e2e-secret";
        await using var database = await SqliteTestDatabase.CreateAsync();
        var dbContextFactory = new TestDbContextFactory(database.Options);
        var requestState = new RequestState();
        await using var server = new LocalServer(async (stream, request, cancellationToken) =>
        {
            requestState.ApiKeyHeader = request.Headers["api-key"];

            await using (var verification = database.CreateContext())
            {
                var messages = await verification.Messages
                    .Include(message => message.Parts)
                    .OrderBy(message => message.Sequence)
                    .ToListAsync(cancellationToken);
                requestState.UserWasCommitted = messages.Count == 2 &&
                    messages[0].Role == MessageRole.User &&
                    messages[0].Status == MessageStatus.Completed;
                requestState.StreamingAssistantWasCommitted = messages.Count == 2 &&
                    messages[1].Role == MessageRole.Assistant &&
                    messages[1].Status == MessageStatus.Streaming &&
                    Assert.IsType<TextMessagePart>(Assert.Single(messages[1].Parts)).Text == string.Empty;
            }

            await WriteAsync(
                stream,
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/event-stream\r\n" +
                "Connection: close\r\n\r\n",
                cancellationToken);
            await WriteAsync(stream, Data("Hel"), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            requestState.FirstFlushSent.TrySetResult();
            await requestState.AllowSecondFlush.Task.WaitAsync(cancellationToken);
            await WriteAsync(
                stream,
                Data("lo") + Finish("stop") + Done(),
                cancellationToken);
        });

        await using var preparationContext = database.CreateContext();
        var clock = new FixedClock(UtcNow);
        var prepare = new PrepareSessionRun(
            new ProjectRepository(preparationContext),
            new SessionRepository(preparationContext),
            new MessageRepository(preparationContext),
            new RunLeaseRepository(preparationContext),
            new UnitOfWork(preparationContext),
            clock,
            new SessionRunOptions { LeaseDuration = TimeSpan.FromMinutes(5) });
        var credentialSession = new RecordingCredentialSession(secret);
        var modelClient = new XiaomiChatModelClient(
            new SingleHttpClientFactory(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }),
            new XiaomiModelOptions
            {
                BaseUrl = new Uri(server.BaseUri, "v1").ToString(),
                FirstByteTimeout = TimeSpan.FromSeconds(2),
                StreamIdleTimeout = TimeSpan.FromSeconds(2),
            },
            new XiaomiSseParser(),
            credentialSession);
        var actualPersistence = new StreamingRunPersistence(dbContextFactory, clock);
        var persistence = new CountingPersistence(actualPersistence);
        var output = new CoordinatedOutputSink(requestState);
        var context = new ProjectContext(
            "/workspace/project",
            "/workspace/project",
            false,
            null,
            ProjectPlatform.Linux,
            UtcNow.Date,
            ProjectId.New());
        var service = new RunPrompt(
            new StubProjectContextFactory(context),
            prepare,
            new ChatModelRequestBuilder(new ChatModelHistoryPolicy()),
            modelClient,
            persistence,
            new RunLeaseRenewalService(
                dbContextFactory,
                clock,
                new SessionRunOptions { LeaseDuration = TimeSpan.FromMinutes(5) }),
            output,
            clock,
            new BlockingDelay(),
            new StreamingRunOptions
            {
                FlushInterval = TimeSpan.FromHours(1),
                FlushCharacterThreshold = 256,
                LeaseRenewInterval = TimeSpan.FromMinutes(1),
            });

        var result = await service.ExecuteAsync(
            new RunPromptRequest("Reply with exactly: Hello", context.ProjectRoot));

        Assert.True(requestState.UserWasCommitted);
        Assert.True(requestState.StreamingAssistantWasCommitted);
        Assert.Equal(secret, requestState.ApiKeyHeader);
        Assert.Equal(["Hel", "lo"], output.Deltas);
        Assert.Equal("Hello", result.Text);
        Assert.Equal(1, credentialSession.AcceptedCalls);
        Assert.Equal(0, persistence.IntermediateFlushCalls);
        Assert.Equal(1, persistence.FinalizationCalls);
        Assert.True(persistence.TotalWriteTransactions < output.Deltas.Count);

        await using var finalContext = database.CreateContext();
        var assistant = await finalContext.Messages
            .Include(message => message.Parts)
            .SingleAsync(message => message.Id == result.AssistantMessageId);
        var session = await finalContext.Sessions
            .SingleAsync(value => value.Id == result.SessionId);
        var lease = await finalContext.RunLeases
            .SingleOrDefaultAsync(value => value.SessionId == result.SessionId);

        Assert.Equal(MessageStatus.Completed, assistant.Status);
        Assert.Equal("Hello", Assert.IsType<TextMessagePart>(Assert.Single(assistant.Parts)).Text);
        Assert.Equal(SessionStatus.Idle, session.Status);
        Assert.Null(lease);
        Assert.DoesNotContain(secret, string.Join(string.Empty, output.Deltas), StringComparison.Ordinal);
    }

    private static string Data(string text) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"{text}\"}},\"finish_reason\":null}}]}}\n\n";

    private static string Finish(string reason) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"{reason}\"}}]}}\n\n";

    private static string Done() => "data: [DONE]\n\n";

    private static Task WriteAsync(
        Stream stream,
        string value,
        CancellationToken cancellationToken)
    {
        return stream.WriteAsync(Encoding.UTF8.GetBytes(value), cancellationToken).AsTask();
    }

    private sealed class RequestState
    {
        public bool UserWasCommitted { get; set; }
        public bool StreamingAssistantWasCommitted { get; set; }
        public string? ApiKeyHeader { get; set; }
        public TaskCompletionSource FirstFlushSent { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowSecondFlush { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CoordinatedOutputSink(RequestState state) : IModelOutputSink
    {
        public List<string> Deltas { get; } = [];

        public async Task WriteDeltaAsync(
            string delta,
            CancellationToken cancellationToken = default)
        {
            Deltas.Add(delta);
            if (Deltas.Count == 1)
            {
                await state.FirstFlushSent.Task.WaitAsync(cancellationToken);
                state.AllowSecondFlush.TrySetResult();
            }
        }

        public Task CompleteAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class CountingPersistence(IStreamingRunPersistence inner)
        : IStreamingRunPersistence
    {
        public int IntermediateFlushCalls { get; private set; }
        public int FinalizationCalls { get; private set; }
        public int TotalWriteTransactions => IntermediateFlushCalls + FinalizationCalls;

        public async Task FlushAssistantTextAsync(
            MessageId assistantMessageId,
            string completeText,
            CancellationToken cancellationToken = default)
        {
            IntermediateFlushCalls++;
            await inner.FlushAssistantTextAsync(
                assistantMessageId,
                completeText,
                cancellationToken);
        }

        public async Task CompleteAsync(
            SessionId sessionId,
            MessageId assistantMessageId,
            AgentPulse.Domain.SessionRuns.RunLeaseId leaseId,
            string completeText,
            CancellationToken cancellationToken = default)
        {
            FinalizationCalls++;
            await inner.CompleteAsync(
                sessionId,
                assistantMessageId,
                leaseId,
                completeText,
                cancellationToken);
        }

        public async Task FailAsync(
            SessionId sessionId,
            MessageId assistantMessageId,
            AgentPulse.Domain.SessionRuns.RunLeaseId leaseId,
            string completeText,
            string failureReason,
            CancellationToken cancellationToken = default)
        {
            FinalizationCalls++;
            await inner.FailAsync(
                sessionId,
                assistantMessageId,
                leaseId,
                completeText,
                failureReason,
                cancellationToken);
        }

        public async Task CancelAsync(
            SessionId sessionId,
            MessageId assistantMessageId,
            AgentPulse.Domain.SessionRuns.RunLeaseId leaseId,
            string completeText,
            CancellationToken cancellationToken = default)
        {
            FinalizationCalls++;
            await inner.CancelAsync(
                sessionId,
                assistantMessageId,
                leaseId,
                completeText,
                cancellationToken);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AgentPulseDbContext> options)
        : IDbContextFactory<AgentPulseDbContext>
    {
        public AgentPulseDbContext CreateDbContext() => new(options);

        public Task<AgentPulseDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }

    private sealed class StubProjectContextFactory(ProjectContext context)
        : IProjectContextFactory
    {
        public Task<ProjectContext> CreateAsync(
            string? startPath = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(context);
        }
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class BlockingDelay : IAsyncDelay
    {
        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingCredentialSession(string credential)
        : IProviderCredentialSession
    {
        public int AcceptedCalls { get; private set; }

        public void Set(string value, ProviderCredentialSource source) =>
            throw new NotSupportedException();

        public string GetRequiredCredential() => credential;

        public Task MarkAcceptedAsync(CancellationToken cancellationToken = default)
        {
            AcceptedCalls++;
            return Task.CompletedTask;
        }

        public Task MarkAuthenticationRejectedAsync(
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed record CapturedRequest(IReadOnlyDictionary<string, string> Headers);

    private sealed class LocalServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serverTask;

        public LocalServer(
            Func<NetworkStream, CapturedRequest, CancellationToken, Task> writer)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            BaseUri = new Uri($"http://127.0.0.1:{endpoint.Port}/");
            _serverTask = RunAsync(writer);
        }

        public Uri BaseUri { get; }

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
            Func<NetworkStream, CapturedRequest, CancellationToken, Task> writer)
        {
            using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
            await using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, _cancellation.Token);
            await writer(stream, request, _cancellation.Token);
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
                    throw new IOException("Client disconnected before headers were complete.");
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

            var contentLength = headers.TryGetValue("Content-Length", out var value)
                ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            var bodyOffset = headerEnd + 4;
            while (bytes.Count - bodyOffset < contentLength)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    throw new IOException("Client disconnected before body was complete.");
                }
                bytes.AddRange(buffer.AsSpan(0, read).ToArray());
            }

            return new CapturedRequest(headers);
        }

        private static int FindHeaderEnd(IReadOnlyList<byte> bytes)
        {
            for (var index = 0; index <= bytes.Count - 4; index++)
            {
                if (bytes[index] == '\r' && bytes[index + 1] == '\n' &&
                    bytes[index + 2] == '\r' && bytes[index + 3] == '\n')
                {
                    return index;
                }
            }
            return -1;
        }
    }
}
