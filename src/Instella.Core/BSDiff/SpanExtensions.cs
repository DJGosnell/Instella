namespace Instella.Core.BSDiff;

internal static class SpanExtensions
{
    public static void WritePackedLong(this Span<byte> span, long y)
    {
        if (span.Length < 8)
            throw new ArgumentException("Span must be at least 8 bytes.", nameof(span));

        unchecked
        {
            if (y < 0)
            {
                y = -y;
                span[7] = (byte)((y >> 56) | 0x80);
            }
            else
            {
                span[7] = (byte)(y >> 56);
            }

            span[6] = (byte)(y >> 48);
            span[5] = (byte)(y >> 40);
            span[4] = (byte)(y >> 32);
            span[3] = (byte)(y >> 24);
            span[2] = (byte)(y >> 16);
            span[1] = (byte)(y >> 8);
            span[0] = (byte)y;
        }
    }

    public static long ReadPackedLong(this Span<byte> span)
    {
        long y = span[7] & 0x7F;
        y <<= 8; y += span[6];
        y <<= 8; y += span[5];
        y <<= 8; y += span[4];
        y <<= 8; y += span[3];
        y <<= 8; y += span[2];
        y <<= 8; y += span[1];
        y <<= 8; y += span[0];

        return (span[7] & 0x80) != 0 ? -y : y;
    }

    public static long ReadPackedLong(this ReadOnlySpan<byte> span)
    {
        long y = span[7] & 0x7F;
        y <<= 8; y += span[6];
        y <<= 8; y += span[5];
        y <<= 8; y += span[4];
        y <<= 8; y += span[3];
        y <<= 8; y += span[2];
        y <<= 8; y += span[1];
        y <<= 8; y += span[0];

        return (span[7] & 0x80) != 0 ? -y : y;
    }

    public static Span<T> SliceUpTo<T>(this Span<T> span, int max) => span[..Math.Min(span.Length, max)];
}
