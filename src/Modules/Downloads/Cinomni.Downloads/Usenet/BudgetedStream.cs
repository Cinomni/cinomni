namespace Cinomni.Downloads.Usenet;

/// <summary>
/// Passes a stream through, and refuses to let more than a set number of bytes be read from it between
/// two calls to <see cref="Allow"/>. What a server sends is hostile input. A limit on lines or on the
/// size of an article is never reached by a server that sends no line end, so the limit is on bytes,
/// and it sits under the reader that would otherwise keep what it is given.
/// </summary>
internal sealed class BudgetedStream(Stream inner) : Stream
{
    private long remaining = long.MaxValue;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>The most that may be read before the next call. Reading less leaves nothing over.</summary>
    public void Allow(long bytes) => remaining = bytes;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Spend(await inner.ReadAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => Spend(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Spend(inner.Read(buffer));

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Spend(int read)
    {
        remaining -= read;
        return remaining >= 0 ? read : throw new InvalidOperationException("The Usenet server sent more than this installation will read.");
    }
}
