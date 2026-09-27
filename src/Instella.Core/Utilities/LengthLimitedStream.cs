namespace Instella.Core.Utilities;

/// <summary>
/// Read-only wrapper that throws once more than <paramref name="limit"/> bytes are read. Every
/// download the client stores reads through one, bounded by what the signed release allows: a
/// hostile server can refuse service, but can't fill a disk or exhaust memory.
/// </summary>
internal sealed class LengthLimitedStream(Stream inner, long limit, string what) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        Count(await inner.ReadAsync(buffer, ct).ConfigureAwait(false));

    private int Count(int n)
    {
        if ((_read += n) > limit)
            throw new InvalidDataException($"{what} is larger than the {limit} bytes expected");
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
