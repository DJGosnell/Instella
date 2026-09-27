using Instella.Core.BSDiff;
using Instella.Core.Diff;
using NUnit.Framework;

namespace Instella.Core.Tests;

/// <summary>
/// Property tests for the streamed patch path. Every patch
/// is applied three ways — in memory, streamed from a seekable file, and streamed from
/// a non-seekable wrapper — and all three must reproduce the new bytes exactly.
/// </summary>
[TestFixture]
public class BsDiffEngineStreamingTests
{
    private static IEnumerable<TestCaseData> Shapes()
    {
        foreach (var size in new[] { 0, 1, 4 * 1024, 1024 * 1024 })
        {
            foreach (var edit in new[] { "insert", "delete", "append", "mutate" })
            {
                foreach (var seed in new[] { 1, 2 })
                    yield return new TestCaseData(size, edit, seed).SetName($"{edit} size={size} seed={seed}");
            }
        }
    }

    [TestCaseSource(nameof(Shapes))]
    public async Task StreamedApply_MatchesInMemoryApply(int size, string edit, int seed)
    {
        var rng = new Random(seed * 7919 + size);
        var oldData = new byte[size];
        rng.NextBytes(oldData);
        var newData = Edit(oldData, edit, rng);

        using var patchBuffer = new MemoryStream();
        BSDiffEncoder.Create(oldData, newData, patchBuffer);
        var patch = patchBuffer.ToArray();

        // 1. In memory.
        using (var output = new MemoryStream())
        {
            BSPatch.Apply(oldData, patch, output);
            Assert.That(output.ToArray(), Is.EqualTo(newData), "in-memory apply");
        }

        // 2. Streamed from a FileStream patch.
        var patchPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(patchPath, patch);
            await using var patchFile = File.OpenRead(patchPath);
            using var old = new MemoryStream(oldData);
            using var output = new MemoryStream();
            await BsDiffEngine.Instance.ApplyPatchAsync(old, patchFile, output);
            Assert.That(output.ToArray(), Is.EqualTo(newData), "streamed (file) apply");
        }
        finally
        {
            File.Delete(patchPath);
        }

        // 3. Streamed from a non-seekable patch (spooled to disk internally).
        using (var old = new MemoryStream(oldData))
        using (var output = new MemoryStream())
        {
            await BsDiffEngine.Instance.ApplyPatchAsync(old, new NonSeekable(patch), output);
            Assert.That(output.ToArray(), Is.EqualTo(newData), "streamed (non-seekable) apply");
        }
    }

    /// <summary>
    /// Three sections opened up front and read interleaved: each section needs its own
    /// stream position, or ctrl tuples are decoded from the extra block and the patch fails
    /// with "copy size exceeds output".
    /// </summary>
    [Test]
    public async Task InterleavedSections_DecodeCorrectly()
    {
        var oldData = new byte[64 * 1024];
        new Random(42).NextBytes(oldData);
        var newData = (byte[])oldData.Clone();
        for (var i = 0; i < newData.Length; i += 4096) newData[i] ^= 0xFF;
        newData = [.. newData, .. "tail"u8.ToArray()];

        using var patch = new MemoryStream();
        BSDiffEncoder.Create(oldData, newData, patch);
        patch.Position = 0;

        using var output = new MemoryStream();
        await BsDiffEngine.Instance.ApplyPatchAsync(new MemoryStream(oldData), patch, output);
        Assert.That(output.ToArray(), Is.EqualTo(newData));
    }

    [Test]
    public async Task StreamedApply_RespectsPatchStreamStartingPosition()
    {
        var oldData = "the quick brown fox"u8.ToArray();
        var newData = "the quick red fox jumps"u8.ToArray();
        using var patchOnly = new MemoryStream();
        BSDiffEncoder.Create(oldData, newData, patchOnly);

        // Patch embedded after a 10-byte prefix, stream positioned at the patch start.
        using var container = new MemoryStream();
        container.Write(new byte[10]);
        container.Write(patchOnly.ToArray());
        container.Position = 10;

        using var output = new MemoryStream();
        await BsDiffEngine.Instance.ApplyPatchAsync(new MemoryStream(oldData), container, output);
        Assert.That(output.ToArray(), Is.EqualTo(newData));
    }

    private static byte[] Edit(byte[] data, string edit, Random rng)
    {
        var list = new List<byte>(data);
        var insert = new byte[Math.Max(1, data.Length / 16)];
        rng.NextBytes(insert);
        switch (edit)
        {
            case "insert":
                list.InsertRange(data.Length == 0 ? 0 : rng.Next(data.Length), insert);
                break;
            case "delete":
                if (data.Length > 0)
                {
                    var at = rng.Next(data.Length);
                    list.RemoveRange(at, Math.Min(Math.Max(1, data.Length / 16), data.Length - at));
                }
                break;
            case "append":
                list.AddRange(insert);
                break;
            case "mutate":
                for (var i = 0; i < list.Count; i += Math.Max(1, list.Count / 32)) list[i] ^= 0x5A;
                break;
        }
        return [.. list];
    }

    private sealed class NonSeekable(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
