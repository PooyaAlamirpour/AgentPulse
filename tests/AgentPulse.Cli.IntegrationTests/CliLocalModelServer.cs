using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AgentPulse.Cli.IntegrationTests;

internal sealed class CliLocalModelServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _serverTask;

    private CliLocalModelServer(bool hangAfterHeaders, int expectedRequestCount)
    {
        if (expectedRequestCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRequestCount));
        }

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        BaseUrl = $"http://127.0.0.1:{endpoint.Port}/v1";
        _serverTask = RunAsync(hangAfterHeaders, expectedRequestCount);
    }

    public string BaseUrl { get; }

    public List<string> RequestBodies { get; } = [];

    public static CliLocalModelServer StartSuccessful(int expectedRequestCount = 1) =>
        new(hangAfterHeaders: false, expectedRequestCount);

    public static CliLocalModelServer StartHanging() =>
        new(hangAfterHeaders: true, expectedRequestCount: 1);

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

    private async Task RunAsync(bool hangAfterHeaders, int expectedRequestCount)
    {
        for (var requestNumber = 0; requestNumber < expectedRequestCount; requestNumber++)
        {
            using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
            await using var stream = client.GetStream();
            RequestBodies.Add(await ReadRequestAsync(stream, _cancellation.Token));
            await WriteAsync(
                stream,
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/event-stream\r\n" +
                "Connection: close\r\n\r\n",
                _cancellation.Token);
            await stream.FlushAsync(_cancellation.Token);

            if (hangAfterHeaders)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _cancellation.Token);
                return;
            }

            await WriteAsync(
                stream,
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hel\"},\"finish_reason\":null}]}\n\n",
                _cancellation.Token);
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

    private static async Task<string> ReadRequestAsync(
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
        var contentLength = headerText
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Split(':', 2))
            .Where(static parts => parts.Length == 2)
            .Where(static parts => string.Equals(
                parts[0].Trim(),
                "Content-Length",
                StringComparison.OrdinalIgnoreCase))
            .Select(static parts => int.Parse(
                parts[1].Trim(),
                System.Globalization.CultureInfo.InvariantCulture))
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

        return Encoding.UTF8.GetString(bytes.Skip(bodyOffset).Take(contentLength).ToArray());
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
}
