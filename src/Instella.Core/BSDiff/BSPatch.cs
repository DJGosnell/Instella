using System.Buffers;
using System.Runtime.InteropServices;

namespace Instella.Core.BSDiff;

using static Constants;

/// <summary>
/// Applies BSDIFF-format patches for binary delta decoding.
/// </summary>
internal static class BSPatch
{
    /// <summary>
    /// Delegate for opening a patch stream at a specific offset.
    /// </summary>
    /// <param name="offset">Zero-based offset into the patch</param>
    /// <param name="length">Length of the stream from offset, or 0 for the rest</param>
    /// <returns>Readable, seekable stream</returns>
    public delegate Stream OpenPatchStream(long offset, long length);

    /// <summary>
    /// Applies a BSDIFF-format patch to an original and produces the updated version.
    /// </summary>
    /// <param name="input">Original (older) data</param>
    /// <param name="diff">BSDIFF-format patch data</param>
    /// <param name="output">Writable stream where the updated data will be written</param>
    public static void Apply(ReadOnlyMemory<byte> input, ReadOnlyMemory<byte> diff, Stream output)
    {
        var newSize = CreatePatchStreams(openPatchStream, out Stream controlStream, out Stream diffStream, out Stream extraStream);

        using var inputStream = AsStream(input);
        ApplyInternal(newSize, inputStream, controlStream, diffStream, extraStream, output);
        return;

        Stream openPatchStream(long offset, long length)
            => AsStream(diff.Slice((int)offset, length > 0 ? (int)length : diff.Length - (int)offset));
    }

    /// <summary>
    /// Applies a BSDIFF-format patch to an original and produces the updated version.
    /// </summary>
    /// <param name="input">Readable, seekable stream of the original (older) data</param>
    /// <param name="openPatchStream">Delegate to open patch sections</param>
    /// <param name="output">Writable stream where the updated data will be written</param>
    /// <param name="maxOutputSize">When set, a header that announces a larger output is refused before any work.</param>
    public static void Apply(Stream input, OpenPatchStream openPatchStream, Stream output, long? maxOutputSize = null)
    {
        var newSize = CreatePatchStreams(openPatchStream, out Stream controlStream, out Stream diffStream, out Stream extraStream);
        if (maxOutputSize is { } max && newSize > max)
        {
            controlStream.Dispose();
            diffStream.Dispose();
            extraStream.Dispose();
            throw new InvalidDataException($"Patch output of {newSize} bytes exceeds the {max} bytes allowed");
        }
        ApplyInternal(newSize, input, controlStream, diffStream, extraStream, output);
    }

    private static long CreatePatchStreams(OpenPatchStream openPatchStream, out Stream ctrl, out Stream diff, out Stream extra)
    {
        long controlLength, diffLength, newSize;
        using (var headerStream = openPatchStream(0, HeaderSize))
        {
            if (!headerStream.CanRead)
                throw new ArgumentException("Patch stream must be readable", nameof(openPatchStream));
            if (!headerStream.CanSeek)
                throw new ArgumentException("Patch stream must be seekable", nameof(openPatchStream));

            Span<byte> header = stackalloc byte[HeaderSize];
            headerStream.ReadExactly(header);

            var signature = header.ReadPackedLong();
            if (signature != Signature)
                throw new InvalidOperationException("Corrupt patch: invalid signature");

            controlLength = header[HeaderOffsetCtrl..].ReadPackedLong();
            diffLength = header[HeaderOffsetDiff..].ReadPackedLong();
            newSize = header[HeaderOffsetNewData..].ReadPackedLong();

            if (controlLength < 0 || diffLength < 0 || newSize < 0)
                throw new InvalidOperationException("Corrupt patch: negative lengths");
        }

        Stream
            compressedControlStream = openPatchStream(HeaderSize, controlLength),
            compressedDiffStream = openPatchStream(HeaderSize + controlLength, diffLength),
            compressedExtraStream = openPatchStream(HeaderSize + controlLength + diffLength, 0);

        ctrl = BSDiffEncoder.GetEncodingStream(compressedControlStream, false);
        diff = BSDiffEncoder.GetEncodingStream(compressedDiffStream, false);
        extra = BSDiffEncoder.GetEncodingStream(compressedExtraStream, false);

        return newSize;
    }

    private static void ApplyInternal(long newSize, Stream input, Stream ctrl, Stream diff, Stream extra, Stream output, int bufferSize = 0x1000)
    {
        if (!input.CanRead)
            throw new ArgumentException("Input stream must be readable", nameof(input));
        if (!input.CanSeek)
            throw new ArgumentException("Input stream must be seekable", nameof(input));
        if (!output.CanWrite)
            throw new ArgumentException("Output stream must be writable", nameof(output));

        using (ctrl)
        using (diff)
        using (extra)
        {
            var diffBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            var inputBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                Span<byte> ctrlBuffer = stackalloc byte[sizeof(long) * 3];

                while (output.Position < newSize)
                {
                    ctrl.ReadExactly(ctrlBuffer);

                    var addSize = ctrlBuffer.ReadPackedLong();
                    var copySize = ctrlBuffer[sizeof(long)..].ReadPackedLong();
                    var seekAmount = ctrlBuffer[(sizeof(long) * 2)..].ReadPackedLong();

                    if (output.Position + addSize > newSize)
                        throw new InvalidOperationException("Corrupt patch: add size exceeds output");

                    while (addSize > 0)
                    {
                        var toRead = (int)Math.Min(addSize, bufferSize);
                        var diffBytesRead = diff.Read(diffBuffer, 0, toRead);
                        if (diffBytesRead == 0)
                            throw new InvalidOperationException(
                                $"Diff stream EOF at byte {diff.Position} (expected {toRead} more bytes for add block)");

                        var inputBytesRead = input.Read(inputBuffer, 0, diffBytesRead);
                        if (inputBytesRead != diffBytesRead)
                            throw new InvalidOperationException(
                                $"Input stream EOF at byte {input.Position} (expected {diffBytesRead} bytes, got {inputBytesRead})");

                        for (var i = 0; i < diffBytesRead; i++)
                            diffBuffer[i] += inputBuffer[i];

                        output.Write(diffBuffer, 0, diffBytesRead);
                        addSize -= diffBytesRead;
                    }

                    if (output.Position + copySize > newSize)
                        throw new InvalidOperationException("Corrupt patch: copy size exceeds output");

                    while (copySize > 0)
                    {
                        var toRead = (int)Math.Min(copySize, bufferSize);
                        var bytesRead = extra.Read(diffBuffer, 0, toRead);
                        if (bytesRead == 0)
                            throw new InvalidOperationException(
                                $"Extra stream EOF at byte {extra.Position} (expected {toRead} more bytes for copy block)");
                        output.Write(diffBuffer, 0, bytesRead);
                        copySize -= bytesRead;
                    }

                    input.Seek(seekAmount, SeekOrigin.Current);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(diffBuffer);
                ArrayPool<byte>.Shared.Return(inputBuffer);
            }
        }

        output.Flush();
    }

    /// <summary>
    /// Creates a readable, seekable MemoryStream over a ReadOnlyMemory without copying
    /// when backed by an array, or with a copy as fallback.
    /// </summary>
    private static MemoryStream AsStream(ReadOnlyMemory<byte> memory)
    {
        if (MemoryMarshal.TryGetArray(memory, out var segment))
            return new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);

        return new MemoryStream(memory.ToArray(), writable: false);
    }
}
