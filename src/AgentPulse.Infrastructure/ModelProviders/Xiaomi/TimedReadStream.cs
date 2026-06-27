using AgentPulse.Application.ChatModels;

namespace AgentPulse.Infrastructure.ModelProviders.Xiaomi;

internal sealed class TimedReadStream(
    Stream innerStream,
    TimeSpan firstByteTimeout,
    TimeSpan idleTimeout) : Stream
{
    private bool _receivedData;

    public override bool CanRead => innerStream.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException(
            "Synchronous reads are not supported for model streaming.");
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        return await ReadAsync(
            buffer.AsMemory(offset, count),
            cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var timeout = _receivedData ? idleTimeout : firstByteTimeout;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            var bytesRead = await innerStream.ReadAsync(buffer, timeoutSource.Token);
            if (bytesRead > 0)
            {
                _receivedData = true;
            }

            return bytesRead;
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            var code = _receivedData
                ? ModelProviderErrorCode.StreamIdleTimeout
                : ModelProviderErrorCode.FirstByteTimeout;
            var message = _receivedData
                ? "Xiaomi MiMo stopped sending stream data before the idle timeout elapsed."
                : "Xiaomi MiMo did not send the first stream data before the timeout elapsed.";

            throw new ModelProviderException(code, message, exception);
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            innerStream.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await innerStream.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
