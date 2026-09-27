using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace Instella.Core.BSDiff;

using static Constants;

/// <summary>
/// Creates BSDIFF-format patches for binary delta encoding.
/// </summary>
internal static class BSDiffEncoder
{
    internal static Stream GetEncodingStream(Stream stream, bool output)
        => output
            ? new BrotliStream(stream, CompressionLevel.Optimal, leaveOpen: true)
            : new BrotliStream(stream, CompressionMode.Decompress, leaveOpen: true);

    /// <summary>
    /// Creates a BSDIFF-format patch from two byte buffers.
    /// </summary>
    /// <param name="oldData">Original (older) data</param>
    /// <param name="newData">Changed (newer) data</param>
    /// <param name="output">Seekable, writable stream where the patch will be written</param>
    public static void Create(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (!output.CanSeek)
            throw new ArgumentException("Output stream must be seekable.", nameof(output));
        if (!output.CanWrite)
            throw new ArgumentException("Output stream must be writable.", nameof(output));

        Span<byte> header = stackalloc byte[HeaderSize];
        header[HeaderOffsetSig..].WritePackedLong(Signature);
        header[HeaderOffsetNewData..].WritePackedLong(newData.Length);

        var startPosition = output.Position;
        output.Write(header);

        Span<byte> buf = stackalloc byte[sizeof(long)];

        var sa = ArrayPool<int>.Shared.Rent(oldData.Length + 1);
        sa.AsSpan(0, oldData.Length + 1).Clear();
        try
        {
            using var ctrlSink = new MemoryStream();
            using var diffSink = new MemoryStream();
            using var extraSink = new MemoryStream();

            {
                using var ctrlEncStream = GetEncodingStream(ctrlSink, true);
                using var diffEncStream = GetEncodingStream(diffSink, true);
                using var extraEncStream = GetEncodingStream(extraSink, true);

                Span<int> I = sa.AsSpan(0, oldData.Length + 1);
                SuffixSort.Sort(oldData, I[..^1]);

                var scan = 0;
                var pos = 0;
                var len = 0;
                var lastscan = 0;
                var lastpos = 0;
                var lastoffset = 0;

                while (scan < newData.Length)
                {
                    var oldscore = 0;

                    for (var scsc = scan += len; scan < newData.Length; scan++)
                    {
                        len = Search(I, oldData, newData[scan..], 0, oldData.Length, out pos);

                        for (; scsc < scan + len; scsc++)
                        {
                            if ((scsc + lastoffset < oldData.Length) && (oldData[scsc + lastoffset] == newData[scsc]))
                                oldscore++;
                        }

                        if ((len == oldscore && len != 0) || (len > oldscore + 8))
                            break;

                        if ((scan + lastoffset < oldData.Length) && (oldData[scan + lastoffset] == newData[scan]))
                            oldscore--;
                    }

                    if (len != oldscore || scan == newData.Length)
                    {
                        var s = 0;
                        var sf = 0;
                        var lenf = 0;
                        for (var i = 0; (lastscan + i < scan) && (lastpos + i < oldData.Length);)
                        {
                            if (oldData[lastpos + i] == newData[lastscan + i])
                                s++;

                            i++;
                            if (s * 2 - i > sf * 2 - lenf)
                            {
                                sf = s;
                                lenf = i;
                            }
                        }

                        var lenb = 0;
                        if (scan < newData.Length)
                        {
                            s = 0;
                            var sb = 0;
                            for (var i = 1; (scan >= lastscan + i) && (pos >= i); i++)
                            {
                                if (oldData[pos - i] == newData[scan - i])
                                    s++;

                                if (s * 2 - i > sb * 2 - lenb)
                                {
                                    sb = s;
                                    lenb = i;
                                }
                            }
                        }

                        if (lastscan + lenf > scan - lenb)
                        {
                            var overlap = (lastscan + lenf) - (scan - lenb);
                            s = 0;
                            var ss = 0;
                            var lens = 0;
                            for (var i = 0; i < overlap; i++)
                            {
                                if (newData[lastscan + lenf - overlap + i] == oldData[lastpos + lenf - overlap + i])
                                    s++;

                                if (newData[scan - lenb + i] == oldData[pos - lenb + i])
                                    s--;

                                if (s > ss)
                                {
                                    ss = s;
                                    lens = i + 1;
                                }
                            }

                            lenf += lens - overlap;
                            lenb -= lens;
                        }

                        for (var i = 0; i < lenf; i++)
                            diffEncStream.WriteByte((byte)(newData[lastscan + i] - oldData[lastpos + i]));

                        var extraLength = (scan - lenb) - (lastscan + lenf);
                        if (extraLength > 0)
                            extraEncStream.Write(newData.Slice(lastscan + lenf, extraLength));

                        buf.WritePackedLong(lenf);
                        ctrlEncStream.Write(buf);

                        buf.WritePackedLong(extraLength);
                        ctrlEncStream.Write(buf);

                        buf.WritePackedLong((pos - lenb) - (lastpos + lenf));
                        ctrlEncStream.Write(buf);

                        lastscan = scan - lenb;
                        lastpos = pos - lenb;
                        lastoffset = pos - scan;
                    }
                }
            }

            output.Write(ctrlSink.GetBuffer().AsSpan(0, (int)ctrlSink.Length));
            header[HeaderOffsetCtrl..].WritePackedLong(ctrlSink.Length);

            output.Write(diffSink.GetBuffer().AsSpan(0, (int)diffSink.Length));
            header[HeaderOffsetDiff..].WritePackedLong(diffSink.Length);

            output.Write(extraSink.GetBuffer().AsSpan(0, (int)extraSink.Length));

            var endPosition = output.Position;
            output.Position = startPosition;
            output.Write(header);
            output.Position = endPosition;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(sa);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        => left.SequenceCompareTo(right);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MatchLength(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData)
        => oldData.CommonPrefixLength(newData);

    private static int Search(ReadOnlySpan<int> I, ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, int start, int end, out int pos)
    {
        while (true)
        {
            if (end - start < 2)
            {
                var x = MatchLength(oldData[I[start]..], newData);
                var y = MatchLength(oldData[I[end]..], newData);

                if (x > y)
                {
                    pos = I[start];
                    return x;
                }
                else
                {
                    pos = I[end];
                    return y;
                }
            }

            var midPoint = start + (end - start) / 2;
            if (CompareBytes(oldData[I[midPoint]..], newData) < 0)
                start = midPoint;
            else
                end = midPoint;
        }
    }
}
