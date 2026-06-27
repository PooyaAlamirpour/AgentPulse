using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AgentPulse.Infrastructure.Tests.ModelProviders.OpenAiCompatible;

internal sealed class LocalModelServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _serverTask;
    private readonly TaskCompletionSource<CapturedHttpRequest> _request =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LocalModelServer(
        Func<NetworkStream, CapturedHttpRequest, CancellationToken, Task> responseWriter)
    {
        ArgumentNullException.ThrowIfNull(responseWriter);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        BaseUri = new Uri($"http://127.0.0.1:{endpoint.Port}/");
        _serverTask = RunAsync(responseWriter);
    }

    public Uri BaseUri { get; }

    public Task<CapturedHttpRequest> Request => _request.Task;

    public bool HasReceivedRequest => _request.Task.IsCompletedSuccessfully;

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

    public static Task WriteSseHeadersAsync(
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

    public static async Task WriteResponseAsync(
        Stream stream,
        int statusCode,
        string reason,
        string body,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var builder = new StringBuilder()
            .Append("HTTP/1.1 ")
            .Append(statusCode)
            .Append(' ')
            .Append(reason)
            .Append("\r\nContent-Length: ")
            .Append(bodyBytes.Length)
            .Append("\r\nConnection: close\r\n");

        if (headers is not null)
        {
            foreach (var header in headers)
            {
                builder.Append(header.Key)
                    .Append(": ")
                    .Append(header.Value)
                    .Append("\r\n");
            }
        }

        builder.Append("\r\n");
        await WriteAsync(stream, builder.ToString(), cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
    }

    public static Task WriteAsync(
        Stream stream,
        string value,
        CancellationToken cancellationToken)
    {
        return stream.WriteAsync(Encoding.UTF8.GetBytes(value), cancellationToken).AsTask();
    }

    private async Task RunAsync(
        Func<NetworkStream, CapturedHttpRequest, CancellationToken, Task> responseWriter)
    {
        using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
        await using var stream = client.GetStream();
        var request = await ReadRequestAsync(stream, _cancellation.Token);
        _request.TrySetResult(request);
        await responseWriter(stream, request, _cancellation.Token);
        await stream.FlushAsync(_cancellation.Token);
    }

    private static async Task<CapturedHttpRequest> ReadRequestAsync(
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
                throw new IOException("The client disconnected before request headers completed.");
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
                throw new IOException("The client disconnected before the request body completed.");
            }

            bytes.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        var body = Encoding.UTF8.GetString(
            bytes.Skip(bodyOffset).Take(contentLength).ToArray());
        return new CapturedHttpRequest(lines[0], headers, body);
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

internal sealed record CapturedHttpRequest(
    string RequestLine,
    IReadOnlyDictionary<string, string> Headers,
    string Body);
