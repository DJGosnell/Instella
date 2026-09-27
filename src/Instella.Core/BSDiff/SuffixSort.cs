using System.Runtime.CompilerServices;

namespace Instella.Core.BSDiff;

/// <summary>
/// Suffix array construction using SAIS (Suffix Array Induced Sorting) algorithm.
/// </summary>
internal sealed class SuffixSort
{
    private const int AlphabetSize = byte.MaxValue + 1;

    public static void Sort(ReadOnlySpan<byte> text, Span<int> suffixes)
    {
        if (suffixes.Length != text.Length)
            throw new ArgumentException("Text and suffix buffers should have the same length");

        if (text.Length <= 1)
        {
            if (text.Length == 1)
                suffixes[0] = 0;
            return;
        }

        SAIS<byte>.Main(new TextAccessor<byte>(text), suffixes, 0, text.Length, AlphabetSize);
    }
}

internal static class SAIS<T> where T : unmanaged, IConvertible
{
    private const int MinBucketSize = byte.MaxValue + 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GetCounts(TextAccessor<T> t, Span<int> c, int n, int k)
    {
        c[..k].Clear();
        for (int i = 0; i < n; ++i)
            c[t[i]]++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GetBuckets(ReadOnlySpan<int> c, Span<int> b, int k, bool end)
    {
        for (int i = 0, sum = 0; i < k; ++i)
        {
            sum += c[i];
            b[i] = end ? sum : sum - c[i];
        }
    }

    private static void LMSSort(TextAccessor<T> t, Span<int> sa, Span<int> c, Span<int> b, int n, int k)
    {
        int bb, i, j;
        int c0, c1;

        if (c == b)
            GetCounts(t, c, n, k);

        GetBuckets(c, b, k, false);

        j = n - 1;
        bb = b[c1 = t[j]];
        --j;
        sa[bb++] = t[j] < c1 ? ~j : j;

        for (i = 0; i < n; ++i)
        {
            if (0 < (j = sa[i]))
            {
                if ((c0 = t[j]) != c1)
                {
                    b[c1] = bb;
                    bb = b[c1 = c0];
                }
                --j;
                sa[bb++] = t[j] < c1 ? ~j : j;
                sa[i] = 0;
            }
            else if (j < 0)
            {
                sa[i] = ~j;
            }
        }

        if (c == b)
            GetCounts(t, c, n, k);

        GetBuckets(c, b, k, true);

        for (i = n - 1, bb = b[c1 = 0]; 0 <= i; --i)
        {
            if (0 < (j = sa[i]))
            {
                if ((c0 = t[j]) != c1)
                {
                    b[c1] = bb;
                    bb = b[c1 = c0];
                }
                --j;
                sa[--bb] = t[j] > c1 ? ~(j + 1) : j;
                sa[i] = 0;
            }
        }
    }

    private static int LMSPostProc(TextAccessor<T> t, Span<int> sa, int n, int m)
    {
        int i, j, p, q;
        int qlen, name;
        int c0, c1;

        for (i = 0; (p = sa[i]) < 0; ++i)
            sa[i] = ~p;

        if (i < m)
        {
            for (j = i, ++i; ; ++i)
            {
                if ((p = sa[i]) < 0)
                {
                    sa[j++] = ~p;
                    sa[i] = 0;
                    if (j == m)
                        break;
                }
            }
        }

        i = n - 1;
        j = n - 1;
        c0 = t[n - 1];
        do { c1 = c0; } while (0 <= --i && (c0 = t[i]) >= c1);

        for (; 0 <= i;)
        {
            do { c1 = c0; } while (0 <= --i && (c0 = t[i]) <= c1);
            if (0 <= i)
            {
                sa[m + (i + 1 >> 1)] = j - i;
                j = i + 1;
                do { c1 = c0; } while (0 <= --i && (c0 = t[i]) >= c1);
            }
        }

        for (i = 0, name = 0, q = n, qlen = 0; i < m; ++i)
        {
            p = sa[i];
            int plen = sa[m + (p >> 1)];
            bool diff = true;

            if (plen == qlen && q + plen < n)
            {
                for (j = 0; j < plen && t[p + j] == t[q + j]; ++j) { }
                if (j == plen)
                    diff = false;
            }

            if (diff)
            {
                ++name;
                q = p;
                qlen = plen;
            }
            sa[m + (p >> 1)] = name;
        }

        return name;
    }

    private static void InduceSA(TextAccessor<T> t, Span<int> sa, Span<int> c, Span<int> b, int n, int k)
    {
        int bb, i, j;
        int c0, c1;

        if (c == b)
            GetCounts(t, c, n, k);

        GetBuckets(c, b, k, false);

        j = n - 1;
        bb = b[c1 = t[j]];
        sa[bb++] = 0 < j && t[j - 1] < c1 ? ~j : j;

        for (i = 0; i < n; ++i)
        {
            j = sa[i];
            sa[i] = ~j;
            if (0 < j)
            {
                if ((c0 = t[--j]) != c1)
                {
                    b[c1] = bb;
                    bb = b[c1 = c0];
                }
                sa[bb++] = 0 < j && t[j - 1] < c1 ? ~j : j;
            }
        }

        if (c == b)
            GetCounts(t, c, n, k);

        GetBuckets(c, b, k, true);

        for (i = n - 1, bb = b[c1 = 0]; 0 <= i; --i)
        {
            if (0 < (j = sa[i]))
            {
                if ((c0 = t[--j]) != c1)
                {
                    b[c1] = bb;
                    bb = b[c1 = c0];
                }
                sa[--bb] = j == 0 || t[j - 1] > c1 ? ~j : j;
            }
            else
            {
                sa[i] = ~j;
            }
        }
    }

    public static void Main(TextAccessor<T> t, Span<int> sa, int fs, int n, int k)
    {
        Span<int> c, b;
        int i, j, bb, m;
        int name;
        int c0, c1;
        uint flags;

        if (k <= MinBucketSize)
        {
            c = new int[k];
            if (k <= fs)
            {
                b = sa[(n + fs - k)..];
                flags = 1;
            }
            else
            {
                b = new int[k];
                flags = 3;
            }
        }
        else if (k <= fs)
        {
            c = sa[(n + fs - k)..];
            if (k <= fs - k)
            {
                b = sa[(n + fs - k * 2)..];
                flags = 0;
            }
            else if (k <= MinBucketSize * 4)
            {
                b = new int[k];
                flags = 2;
            }
            else
            {
                b = c;
                flags = 8;
            }
        }
        else
        {
            c = b = new int[k];
            flags = 4 | 8;
        }

        GetCounts(t, c, n, k);
        GetBuckets(c, b, k, true);

        sa[..n].Clear();

        bb = -1;
        i = n - 1;
        j = n;
        m = 0;
        c0 = t[n - 1];
        do { c1 = c0; } while (0 <= --i && (c0 = t[i]) >= c1);

        for (; 0 <= i;)
        {
            do { c1 = c0; } while (0 <= --i && (c0 = t[i]) <= c1);
            if (0 <= i)
            {
                if (0 <= bb)
                    sa[bb] = j;
                bb = --b[c1];
                j = i;
                ++m;
                do { c1 = c0; } while (0 <= --i && (c0 = t[i]) >= c1);
            }
        }

        if (1 < m)
        {
            LMSSort(t, sa, c, b, n, k);
            name = LMSPostProc(t, sa, n, m);
        }
        else if (m == 1)
        {
            sa[bb] = j + 1;
            name = 1;
        }
        else
        {
            name = 0;
        }

        if (name < m)
        {
            if ((flags & 4) != 0)
            {
                c = null!;
                b = null!;
            }
            if ((flags & 2) != 0)
            {
                b = null!;
            }

            int newfs = n + fs - m * 2;
            if ((flags & (1 | 4 | 8)) == 0)
            {
                if (k + name <= newfs)
                    newfs -= k;
                else
                    flags |= 8;
            }

            for (i = m + (n >> 1) - 1, j = m * 2 + newfs - 1; m <= i; --i)
            {
                if (sa[i] != 0)
                    sa[j--] = sa[i] - 1;
            }

            SAIS<int>.Main(new TextAccessor<int>(sa[(m + newfs)..]), sa, newfs, m, name);

            i = n - 1;
            j = m * 2 - 1;
            c0 = t[n - 1];
            do { c1 = c0; } while (0 <= --i && (c0 = t[i]) >= c1);

            for (; 0 <= i;)
            {
                do { c1 = c0; } while (0 <= --i && (c0 = t[i]) <= c1);
                if (0 <= i)
                {
                    sa[j--] = i + 1;
                    do { c1 = c0; } while (0 <= --i && (c0 = t[i]) >= c1);
                }
            }

            for (i = 0; i < m; ++i)
                sa[i] = sa[m + sa[i]];

            if ((flags & 4) != 0)
                c = b = new int[k];
            if ((flags & 2) != 0)
                b = new int[k];
        }

        if ((flags & 8) != 0)
            GetCounts(t, c, n, k);

        if (1 < m)
        {
            GetBuckets(c, b, k, true);
            i = m - 1;
            j = n;
            int p = sa[m - 1];
            c1 = t[p];
            do
            {
                int q = b[c0 = c1];
                while (q < j)
                    sa[--j] = 0;
                do
                {
                    sa[--j] = p;
                    if (--i < 0)
                        break;
                    p = sa[i];
                } while ((c1 = t[p]) == c0);
            } while (0 <= i);

            while (0 < j)
                sa[--j] = 0;
        }

        InduceSA(t, sa, c, b, n, k);
    }
}
