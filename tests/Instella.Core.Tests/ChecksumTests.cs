using System.Text;
using System.Threading;
using Instella.Core.Utilities;
using NUnit.Framework;

namespace Instella.Core.Tests;

/// <summary>
/// <see cref="Checksum"/> backs the server's content-addressed dedup and the
/// upload flow's file identity: two builds are considered the same artifact when
/// these strings match. The exact digest and its <em>textual form</em> are both
/// part of that contract — a switch to uppercase hex would silently stop every
/// stored hash from matching — so the vectors below are asserted literally
/// rather than by round-tripping the implementation against itself.
/// </summary>
[TestFixture]
public class ChecksumTests
{
    // NIST vectors for the canonical inputs.
    private const string HashOfEmpty =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string HashOfAbc =
        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private static byte[] Abc => Encoding.ASCII.GetBytes("abc");

    [Test]
    public void ComputeSHA256_byteArray_matchesKnownVector()
    {
        Assert.That(Checksum.ComputeSHA256(Abc), Is.EqualTo(HashOfAbc));
    }

    [Test]
    public void ComputeSHA256_emptyByteArray_matchesKnownVector()
    {
        Assert.That(Checksum.ComputeSHA256(Array.Empty<byte>()), Is.EqualTo(HashOfEmpty));
    }

    [Test]
    public void ComputeSHA256_span_matchesKnownVector()
    {
        Assert.That(Checksum.ComputeSHA256(Abc.AsSpan()), Is.EqualTo(HashOfAbc));
    }

    [Test]
    public void ComputeSHA256_emptySpan_matchesKnownVector()
    {
        Assert.That(Checksum.ComputeSHA256(ReadOnlySpan<byte>.Empty), Is.EqualTo(HashOfEmpty));
    }

    [Test]
    public void ComputeSHA256_spanAndArrayOverloads_agree()
    {
        var data = new byte[512];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 7);
        }

        Assert.That(Checksum.ComputeSHA256(data.AsSpan()), Is.EqualTo(Checksum.ComputeSHA256(data)));
    }

    [Test]
    public void ComputeSHA256_output_isLowercaseHexOf32Bytes()
    {
        var hash = Checksum.ComputeSHA256(Abc);

        Assert.That(hash, Has.Length.EqualTo(64), "SHA-256 is 32 bytes = 64 hex characters.");
        Assert.That(hash, Is.EqualTo(hash.ToLowerInvariant()));
        Assert.That(hash, Does.Match("^[0-9a-f]{64}$"));
    }

    [Test]
    public async Task ComputeSHA256Async_stream_matchesKnownVector()
    {
        using var stream = new MemoryStream(Abc);

        Assert.That(await Checksum.ComputeSHA256Async(stream), Is.EqualTo(HashOfAbc));
    }

    [Test]
    public async Task ComputeSHA256Async_emptyStream_matchesKnownVector()
    {
        using var stream = new MemoryStream();

        Assert.That(await Checksum.ComputeSHA256Async(stream), Is.EqualTo(HashOfEmpty));
    }

    [Test]
    public async Task ComputeSHA256Async_streamAndArrayOverloads_agree()
    {
        var data = Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog");
        using var stream = new MemoryStream(data);

        Assert.That(await Checksum.ComputeSHA256Async(stream), Is.EqualTo(Checksum.ComputeSHA256(data)));
    }

    /// <summary>
    /// The stream overload hashes from the stream's <em>current position</em>, not
    /// from the beginning — it does not seek first. Callers that hand over an
    /// already-read stream get the hash of the remainder, which would silently
    /// produce a wrong artifact identity. Pinned deliberately so the behaviour
    /// cannot change unnoticed.
    /// </summary>
    [Test]
    public async Task ComputeSHA256Async_streamNotAtPositionZero_hashesOnlyTheRemainder()
    {
        var data = Encoding.ASCII.GetBytes("XYZabc");
        using var stream = new MemoryStream(data);
        stream.Position = 3;

        var hash = await Checksum.ComputeSHA256Async(stream);

        Assert.That(hash, Is.EqualTo(HashOfAbc), "should hash \"abc\", the bytes after the position");
        Assert.That(hash, Is.Not.EqualTo(Checksum.ComputeSHA256(data)));
    }

    /// <summary>
    /// The server hashes uploaded artifacts of arbitrary size, so the token has to
    /// reach <see cref="System.Security.Cryptography.SHA256.HashDataAsync(Stream, CancellationToken)"/>
    /// or an in-flight hash becomes uncancellable. A refactor that quietly dropped
    /// <c>ct</c> on the way through would keep every other test in this fixture green.
    /// </summary>
    [Test]
    public void ComputeSHA256Async_alreadyCancelledToken_throwsOperationCanceled()
    {
        using var stream = new MemoryStream(new byte[1024 * 1024]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.That(
            async () => await Checksum.ComputeSHA256Async(stream, cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public void VerifySHA256Async_alreadyCancelledToken_throwsOperationCanceled()
    {
        using var stream = new MemoryStream(new byte[1024 * 1024]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.That(
            async () => await Checksum.VerifySHA256Async(stream, HashOfAbc, cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task ComputeSHA256Async_uncancelledToken_completesNormally()
    {
        using var stream = new MemoryStream(Abc);
        using var cts = new CancellationTokenSource();

        Assert.That(await Checksum.ComputeSHA256Async(stream, cts.Token), Is.EqualTo(HashOfAbc));
    }

    [Test]
    public async Task VerifySHA256Async_matchingHash_returnsTrue()
    {
        using var stream = new MemoryStream(Abc);

        Assert.That(await Checksum.VerifySHA256Async(stream, HashOfAbc), Is.True);
    }

    [Test]
    public async Task VerifySHA256Async_mismatchedHash_returnsFalse()
    {
        using var stream = new MemoryStream(Abc);

        Assert.That(await Checksum.VerifySHA256Async(stream, HashOfEmpty), Is.False);
    }

    /// <summary>
    /// Comparison is <see cref="StringComparison.OrdinalIgnoreCase"/>, so a hash
    /// captured from a tool that emits uppercase hex still verifies. Only the
    /// digest has to match, not its casing.
    /// </summary>
    [Test]
    public async Task VerifySHA256Async_uppercaseExpectedHash_stillMatches()
    {
        using var stream = new MemoryStream(Abc);

        Assert.That(await Checksum.VerifySHA256Async(stream, HashOfAbc.ToUpperInvariant()), Is.True);
    }

    [Test]
    public async Task VerifySHA256Async_emptyExpectedHash_returnsFalse()
    {
        using var stream = new MemoryStream(Abc);

        Assert.That(await Checksum.VerifySHA256Async(stream, string.Empty), Is.False);
    }
}
