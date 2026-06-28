using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AgentPulse.Cli.IntegrationTests;

internal sealed class CliLocalModelServer : IAsyncDisposable
{
    private const string PartialText =
        "Partial response checkpoint " +
        "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" +
        "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" +
        "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" +
        "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentQueue<string> _requestBodies = new();
    private readonly TaskCompletionSource<bool> _requestReceived = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _partialResponseSent = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ServerScenario _scenario;
    private readonly int _expectedRequestCount;
    private readonly HttpStatusCode _statusCode;
    private readonly string _errorBody;
    private readonly string? _expectedAuthorizationHeader;
    private readonly Task _serverTask;
    private int _expectedAuthorizationReceived;

    private CliLocalModelServer(
        ServerScenario scenario,
        int expectedRequestCount,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string errorBody = "",
        string? expectedCredential = null)
    {
        if (expectedRequestCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRequestCount));
        }

        _scenario = scenario;
        _expectedRequestCount = expectedRequestCount;
        _statusCode = statusCode;
        _errorBody = errorBody;
        _expectedAuthorizationHeader = expectedCredential is null
            ? null
            : $"Bearer {expectedCredential}";
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        BaseUrl = $"http://127.0.0.1:{endpoint.Port}/v1";
        _serverTask = RunAsync();
    }

    public string BaseUrl { get; }

    public IReadOnlyList<string> RequestBodies => _requestBodies.ToArray();

    public bool ExpectedAuthorizationReceived =>
        Volatile.Read(ref _expectedAuthorizationReceived) != 0;

    public Task RequestReceived => _requestReceived.Task;

    public Task PartialResponseSent => _partialResponseSent.Task;

    public static string ExpectedPartialText => PartialText;

    public static CliLocalModelServer StartSuccessful(int expectedRequestCount = 1) =>
        new(ServerScenario.Success, expectedRequestCount);

    public static CliLocalModelServer StartSuccessfulWithExpectedCredential(
        string credential,
        int expectedRequestCount = 1) =>
        new(
            ServerScenario.Success,
            expectedRequestCount,
            expectedCredential: credential);

    public static CliLocalModelServer StartHanging() =>
        new(ServerScenario.HangBeforeFirstToken, expectedRequestCount: 1);

    public static CliLocalModelServer StartHangingAfterPartial() =>
        new(ServerScenario.HangAfterPartial, expectedRequestCount: 1);

    public static CliLocalModelServer StartPartialThenFailure() =>
        new(ServerScenario.PartialThenInvalidResponse, expectedRequestCount: 1);

    public static CliLocalModelServer StartError(
        HttpStatusCode statusCode,
        string errorBody = "{\"error\":{\"type\":\"api_error\",\"code\":\"test\"}}") =>
        new(
            ServerScenario.ErrorResponse,
            expectedRequestCount: 1,
            statusCode,
            errorBody);

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

    private async Task RunAsync()
    {
        for (var requestNumber = 0; requestNumber < _expectedRequestCount; requestNumber++)
        {
            using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
            await using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, _cancellation.Token);
            _requestBodies.Enqueue(request.Body);
            if (_expectedAuthorizationHeader is not null &&
                string.Equals(
                    request.AuthorizationHeader,
                    _expectedAuthorizationHeader,
                    StringComparison.Ordinal))
            {
                Volatile.Write(ref _expectedAuthorizationReceived, 1);
            }
            _requestReceived.TrySetResult(true);

            if (_scenario == ServerScenario.ErrorResponse)
            {
                await WriteErrorResponseAsync(stream, _cancellation.Token);
                continue;
            }

            await WriteAsync(
                stream,
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/event-stream\r\n" +
                "Cache-Control: no-cache\r\n" +
                "Connection: close\r\n\r\n",
                _cancellation.Token);
            await stream.FlushAsync(_cancellation.Token);

            if (_scenario == ServerScenario.HangBeforeFirstToken)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _cancellation.Token);
                return;
            }

            if (_scenario is ServerScenario.HangAfterPartial or
                ServerScenario.PartialThenInvalidResponse)
            {
                await WriteDeltaAsync(stream, PartialText, _cancellation.Token);
                await stream.FlushAsync(_cancellation.Token);
                _partialResponseSent.TrySetResult(true);

                if (_scenario == ServerScenario.HangAfterPartial)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, _cancellation.Token);
                    return;
                }

                await WriteAsync(
                    stream,
                    "data: {not-valid-json}\n\n",
                    _cancellation.Token);
                await stream.FlushAsync(_cancellation.Token);
                continue;
            }

            await WriteDeltaAsync(stream, "Hel", _cancellation.Token);
            await stream.FlushAsync(_cancellation.Token);
            await Task.Yield();
            await WriteAsync(
                stream,
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"lo\"},\"finish_reason\":null}]}\n\n" +
                "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: [DONE]\n\n",
                _cancellation.Token);
            await stream.FlushAsync(_cancellation.Token);
        }
    }

    private async Task WriteErrorResponseAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(_errorBody);
        var reason = _statusCode switch
        {
            HttpStatusCode.Unauthorized => "Unauthorized",
            HttpStatusCode.Forbidden => "Forbidden",
            HttpStatusCode.TooManyRequests => "Too Many Requests",
            HttpStatusCode.ServiceUnavailable => "Service Unavailable",
            _ => "Error",
        };
        await WriteAsync(
            stream,
            $"HTTP/1.1 {(int)_statusCode} {reason}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n",
            cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static Task WriteDeltaAsync(
        Stream stream,
        string text,
        CancellationToken cancellationToken)
    {
        var encodedText = JsonSerializer.Serialize(text);
        return WriteAsync(
            stream,
            $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"content\":{encodedText}}},\"finish_reason\":null}}]}}\n\n",
            cancellationToken);
    }

    private static async Task<RequestCapture> ReadRequestAsync(
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
                throw new IOException("The CLI disconnected before request headers were complete.");
            }

            bytes.AddRange(buffer.AsSpan(0, read).ToArray());
            headerEnd = FindHeaderEnd(bytes);
        }

        var headerText = Encoding.ASCII.GetString(bytes.Take(headerEnd).ToArray());
        var headers = headerText
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Split(':', 2))
            .Where(static parts => parts.Length == 2)
            .ToArray();
        var contentLength = headers
            .Where(static parts => string.Equals(
                parts[0].Trim(),
                "Content-Length",
                StringComparison.OrdinalIgnoreCase))
            .Select(static parts => int.Parse(
                parts[1].Trim(),
                System.Globalization.CultureInfo.InvariantCulture))
            .SingleOrDefault();
        var authorizationHeader = headers
            .Where(static parts => string.Equals(
                parts[0].Trim(),
                "Authorization",
                StringComparison.OrdinalIgnoreCase))
            .Select(static parts => parts[1].Trim())
            .SingleOrDefault();

        var bodyOffset = headerEnd + 4;
        while (bytes.Count - bodyOffset < contentLength)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                throw new IOException("The CLI disconnected before request body was complete.");
            }

            bytes.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return new RequestCapture(
            Encoding.UTF8.GetString(bytes.Skip(bodyOffset).Take(contentLength).ToArray()),
            authorizationHeader);
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

    private static Task WriteAsync(
        Stream stream,
        string value,
        CancellationToken cancellationToken)
    {
        return stream.WriteAsync(Encoding.UTF8.GetBytes(value), cancellationToken).AsTask();
    }

    private sealed record RequestCapture(string Body, string? AuthorizationHeader);

    private enum ServerScenario
    {
        Success,
        HangBeforeFirstToken,
        HangAfterPartial,
        PartialThenInvalidResponse,
        ErrorResponse,
    }
}
