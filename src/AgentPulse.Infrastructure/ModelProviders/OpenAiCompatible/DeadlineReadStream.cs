using AgentPulse.Application.ChatModels;

namespace AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

internal sealed class DeadlineReadStream(
    Stream innerStream,
    CancellationToken firstByteDeadlineToken,
    TimeSpan firstByteTimeout,
    TimeSpan idleTimeout,
    Action firstByteReceived) : Stream
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
        using var readCancellation = _receivedData
            ? CreateIdleCancellation(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                firstByteDeadlineToken);

        try
        {
            var bytesRead = await innerStream.ReadAsync(buffer, readCancellation.Token);
            if (bytesRead > 0 && !_receivedData)
            {
                _receivedData = true;
                firstByteReceived();
            }

            return bytesRead;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            var message = _receivedData
                ? $"The model endpoint stopped sending stream data for longer than the configured idle timeout of {idleTimeout}."
                : $"The model endpoint did not send the first stream data before the configured timeout of {firstByteTimeout}.";

            throw new ModelProviderException(
                ModelProviderErrorCode.Timeout,
                message,
                exception);
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

    private CancellationTokenSource CreateIdleCancellation(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(idleTimeout);
        return source;
    }
}
