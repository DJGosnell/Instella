using Instella.Core.BSDiff;

namespace Instella.Core.Diff;

/// <summary>
/// BSDiff implementation of IDiffEngine.
/// BSDiff is particularly effective for executables and binaries where code shifts
/// but doesn't fundamentally change between versions.
/// </summary>
internal sealed class BsDiffEngine : IDiffEngine
{
    /// <summary>Singleton instance.</summary>
    public static BsDiffEngine Instance { get; } = new();

    private BsDiffEngine() { }

    public Task CreatePatchAsync(
        Stream oldFile,
        Stream newFile,
        Stream patchOutput,
        IProgress<DiffProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();

            var oldLength = oldFile.CanSeek ? oldFile.Length : 0;
            var newLength = newFile.CanSeek ? newFile.Length : 0;
            var totalReadSize = oldLength + newLength;

            // Read old file with progress
            progress?.Report(new DiffProgress(0, totalReadSize, DiffPhase.ReadingOldFile));
            var oldData = await ReadStreamWithProgressAsync(oldFile, progress, DiffPhase.ReadingOldFile, 0, totalReadSize, ct);

            ct.ThrowIfCancellationRequested();

            // Read new file with progress
            progress?.Report(new DiffProgress(oldLength, totalReadSize, DiffPhase.ReadingNewFile));
            var newData = await ReadStreamWithProgressAsync(newFile, progress, DiffPhase.ReadingNewFile, oldLength, totalReadSize, ct);

            ct.ThrowIfCancellationRequested();

            // Create the patch (BSDiff algorithm requires full data in memory for suffix sorting)
            progress?.Report(new DiffProgress(0, 1, DiffPhase.Processing));
            BSDiffEncoder.Create(oldData, newData, patchOutput);

            progress?.Report(new DiffProgress(1, 1, DiffPhase.Complete));
        }, ct);
    }

    public Task ApplyPatchAsync(
        Stream oldFile,
        Stream patch,
        Stream newFileOutput,
        IProgress<DiffProgress>? progress = null,
        CancellationToken ct = default,
        long? maxOutputSize = null)
    {
        return Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();

            // BSPatch reads the ctrl, diff and extra sections in interleaved order, so each
            // section needs its own position over the shared patch stream. That requires a
            // seekable patch; spool a non-seekable one (e.g. a network stream) to disk.
            var seekablePatch = patch;
            FileStream? spooled = null;
            if (!patch.CanSeek)
            {
                spooled = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite,
                    FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                await patch.CopyToAsync(spooled, ct).ConfigureAwait(false);
                spooled.Position = 0;
                seekablePatch = spooled;
            }

            try
            {
                progress?.Report(new DiffProgress(0, 1, DiffPhase.Processing));
                var basePosition = seekablePatch.Position;
                var gate = new object();
                BSPatch.Apply(
                    oldFile,
                    (offset, length) => new SectionStream(
                        seekablePatch,
                        basePosition + offset,
                        length > 0 ? length : seekablePatch.Length - basePosition - offset,
                        gate),
                    newFileOutput,
                    maxOutputSize);
                progress?.Report(new DiffProgress(1, 1, DiffPhase.Complete));
            }
            finally
            {
                if (spooled is not null) await spooled.DisposeAsync().ConfigureAwait(false);
            }
        }, ct);
    }

    private static async Task<byte[]> ReadStreamWithProgressAsync(
        Stream stream,
        IProgress<DiffProgress>? progress,
        DiffPhase phase,
        long baseProgress,
        long totalProgress,
        CancellationToken ct)
    {
        if (stream is MemoryStream ms && ms.TryGetBuffer(out var buffer))
        {
            // Fast path for MemoryStream - avoid copy
            return buffer.ToArray();
        }

        var length = stream.CanSeek ? (int)stream.Length : 0;
        using var output = length > 0 ? new MemoryStream(length) : new MemoryStream();

        var readBuffer = new byte[81920];
        int bytesRead;
        long totalBytesRead = 0;

        while ((bytesRead = await stream.ReadAsync(readBuffer, ct)) > 0)
        {
            await output.WriteAsync(readBuffer.AsMemory(0, bytesRead), ct);
            totalBytesRead += bytesRead;
            progress?.Report(new DiffProgress(baseProgress + totalBytesRead, totalProgress, phase));
        }

        return output.ToArray();
    }

    /// <summary>
    /// A read-only window <c>[start, start + length)</c> over a shared seekable stream.
    /// Each instance tracks its own position and re-seeks the inner stream before every
    /// read, so several windows can be read in interleaved order. The Brotli decoders
    /// wrapped around each section read in chunks, so this costs one seek per refill.
    /// </summary>
    private sealed class SectionStream(Stream inner, long start, long length, object gate) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, length);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var remaining = length - _position;
            if (remaining <= 0) return 0;

            var slice = buffer[..(int)Math.Min(buffer.Length, remaining)];
            int read;
            // BSPatch reads on one thread; the lock documents and guards the shared-seek invariant.
            lock (gate)
            {
                inner.Position = start + _position;
                read = inner.Read(slice);
            }
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
