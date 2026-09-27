namespace Instella.Core.Diff;

/// <summary>
/// A stream wrapper that reports progress as data is read.
/// </summary>
internal sealed class ProgressReportingStream : Stream
{
    private readonly Stream _inner;
    private readonly IProgress<DiffProgress>? _progress;
    private readonly long _totalLength;
    private readonly DiffPhase _phase;
    private long _bytesRead;

    public ProgressReportingStream(Stream inner, IProgress<DiffProgress>? progress, DiffPhase phase)
    {
        _inner = inner;
        _progress = progress;
        _phase = phase;
        _totalLength = inner.CanSeek ? inner.Length : 0;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        _bytesRead += read;
        ReportProgress();
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        _bytesRead += read;
        ReportProgress();
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var read = await _inner.ReadAsync(buffer, offset, count, ct);
        _bytesRead += read;
        ReportProgress();
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = await _inner.ReadAsync(buffer, ct);
        _bytesRead += read;
        ReportProgress();
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    private void ReportProgress()
    {
        _progress?.Report(new DiffProgress(_bytesRead, _totalLength, _phase));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }
}
