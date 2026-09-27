using Instella.Core.BSDiff;
using NUnit.Framework;

namespace Instella.Core.Tests;

[TestFixture]
public class BSDiffTests
{
    [Test]
    public void CreateAndApplyPatch_IdenticalFiles_ProducesValidPatch()
    {
        var oldData = "Hello, World!"u8.ToArray();
        var newData = "Hello, World!"u8.ToArray();

        using var patchStream = new MemoryStream();
        BSDiffEncoder.Create(oldData, newData, patchStream);

        patchStream.Position = 0;
        using var outputStream = new MemoryStream();
        BSPatch.Apply(oldData, patchStream.ToArray(), outputStream);

        Assert.That(outputStream.ToArray(), Is.EqualTo(newData));
    }

    [Test]
    public void Apply_HeaderAnnouncingMoreThanTheCap_IsRefusedBeforeAnyWork()
    {
        // The patch is unsigned; its header's newSize must not exceed the signed file size.
        var oldData = "Hello, World!"u8.ToArray();
        var newData = new byte[4096];
        using var patchStream = new MemoryStream();
        BSDiffEncoder.Create(oldData, newData, patchStream);
        var patch = patchStream.ToArray();
        using var output = new MemoryStream();

        var ex = Assert.Throws<InvalidDataException>(() => BSPatch.Apply(new MemoryStream(oldData),
            (offset, length) => new MemoryStream(patch, (int)offset, length > 0 ? (int)length : patch.Length - (int)offset),
            output, maxOutputSize: 100));

        Assert.That(ex!.Message, Does.Contain("4096"));
        Assert.That(output.Length, Is.Zero);
    }

    [Test]
    public void CreateAndApplyPatch_SmallChange_ProducesValidPatch()
    {
        var oldData = "Hello, World!"u8.ToArray();
        var newData = "Hello, Universe!"u8.ToArray();

        using var patchStream = new MemoryStream();
        BSDiffEncoder.Create(oldData, newData, patchStream);

        patchStream.Position = 0;
        using var outputStream = new MemoryStream();
        BSPatch.Apply(oldData, patchStream.ToArray(), outputStream);

        Assert.That(outputStream.ToArray(), Is.EqualTo(newData));
    }

    [Test]
    public void CreateAndApplyPatch_CompletelyDifferentFiles_ProducesValidPatch()
    {
        var oldData = "AAAAAAAAAA"u8.ToArray();
        var newData = "BBBBBBBBBB"u8.ToArray();

        using var patchStream = new MemoryStream();
        BSDiffEncoder.Create(oldData, newData, patchStream);

        patchStream.Position = 0;
        using var outputStream = new MemoryStream();
        BSPatch.Apply(oldData, patchStream.ToArray(), outputStream);

        Assert.That(outputStream.ToArray(), Is.EqualTo(newData));
    }

    [Test]
    public void CreateAndApplyPatch_AppendedData_ProducesValidPatch()
    {
        var oldData = "Hello"u8.ToArray();
        var newData = "Hello, World!"u8.ToArray();

        using var patchStream = new MemoryStream();
        BSDiffEncoder.Create(oldData, newData, patchStream);

        patchStream.Position = 0;
        using var outputStream = new MemoryStream();
        BSPatch.Apply(oldData, patchStream.ToArray(), outputStream);

        Assert.That(outputStream.ToArray(), Is.EqualTo(newData));
    }

    [Test]
    public void CreateAndApplyPatch_TruncatedData_ProducesValidPatch()
    {
        var oldData = "Hello, World!"u8.ToArray();
        var newData = "Hello"u8.ToArray();

        using var patchStream = new MemoryStream();
        BSDiffEncoder.Create(oldData, newData, patchStream);

        patchStream.Position = 0;
        using var outputStream = new MemoryStream();
        BSPatch.Apply(oldData, patchStream.ToArray(), outputStream);

        Assert.That(outputStream.ToArray(), Is.EqualTo(newData));
    }

    [Test]
    public void CreateAndApplyPatch_EmptyToNonEmpty_ProducesValidPatch()
    {
        var oldData = Array.Empty<byte>();
        var newData = "Hello, World!"u8.ToArray();

        using var patchStream = new MemoryStream();
        BSDiffEncoder.Create(oldData, newData, patchStream);

        patchStream.Position = 0;
        using var outputStream = new MemoryStream();
        BSPatch.Apply(oldData, patchStream.ToArray(), outputStream);

        Assert.That(outputStream.ToArray(), Is.EqualTo(newData));
    }

    [Test]
    public void CreateAndApplyPatch_NonEmptyToEmpty_ProducesValidPatch()
    {
        var oldData = "Hello, World!"u8.ToArray();
        var newData = Array.Empty<byte>();

        using var patchStream = new MemoryStream();
        BSDiffEncoder.Create(oldData, newData, patchStream);

        patchStream.Position = 0;
        using var outputStream = new MemoryStream();
        BSPatch.Apply(oldData, patchStream.ToArray(), outputStream);

        Assert.That(outputStream.ToArray(), Is.EqualTo(newData));
    }

    [Test]
    public void CreateAndApplyPatch_LargerBinaryData_ProducesValidPatch()
    {
        // Create some pseudo-binary data
        var random = new Random(42); // Fixed seed for reproducibility
        var oldData = new byte[4096];
        random.NextBytes(oldData);

        // Modify ~10% of the data
        var newData = (byte[])oldData.Clone();
        for (int i = 0; i < 400; i++)
        {
            newData[random.Next(newData.Length)] = (byte)random.Next(256);
        }

        using var patchStream = new MemoryStream();
        BSDiffEncoder.Create(oldData, newData, patchStream);

        patchStream.Position = 0;
        using var outputStream = new MemoryStream();
        BSPatch.Apply(oldData, patchStream.ToArray(), outputStream);

        Assert.That(outputStream.ToArray(), Is.EqualTo(newData));
    }

    [Test]
    public void CreateAndApplyPatch_RepetitiveData_ProducesValidPatch()
    {
        // Repetitive data should compress well
        var oldData = new byte[1000];
        for (int i = 0; i < oldData.Length; i++)
            oldData[i] = (byte)(i % 10);

        var newData = new byte[1000];
        for (int i = 0; i < newData.Length; i++)
            newData[i] = (byte)((i + 1) % 10);

        using var patchStream = new MemoryStream();
        BSDiffEncoder.Create(oldData, newData, patchStream);

        patchStream.Position = 0;
        using var outputStream = new MemoryStream();
        BSPatch.Apply(oldData, patchStream.ToArray(), outputStream);

        Assert.That(outputStream.ToArray(), Is.EqualTo(newData));
    }

    [Test]
    public void Create_NullOutputStream_ThrowsArgumentNullException()
    {
        var oldData = "test"u8.ToArray();
        var newData = "test"u8.ToArray();

        Assert.Throws<ArgumentNullException>(() => BSDiffEncoder.Create(oldData, newData, null!));
    }

    [Test]
    public void Create_NonSeekableStream_ThrowsArgumentException()
    {
        var oldData = "test"u8.ToArray();
        var newData = "test"u8.ToArray();

        using var stream = new NonSeekableStream();
        Assert.Throws<ArgumentException>(() => BSDiffEncoder.Create(oldData, newData, stream));
    }

    [Test]
    public void Apply_NegativeNewSize_ThrowsInvalidOperation()
    {
        var oldData = "Hello"u8.ToArray();
        var newData = "World"u8.ToArray();

        using var patchStream = new MemoryStream();
        BSDiffEncoder.Create(oldData, newData, patchStream);

        // Byte 31 is the high byte of the offtin-encoded newSize; its top bit is the sign.
        var patchBytes = patchStream.ToArray();
        patchBytes[31] = 0x80;

        using var outputStream = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() =>
            BSPatch.Apply(oldData, patchBytes, outputStream));
    }

    [Test]
    public void Apply_Byte31IsHighByteOfNewSize_NotAVersionByte()
    {
        var oldData = "Hello"u8.ToArray();
        var newData = "World"u8.ToArray();

        using var patchStream = new MemoryStream();
        BSDiffEncoder.Create(oldData, newData, patchStream);

        // BSDIFF40 has no version byte: offset 31 is the high byte of newSize. Setting
        // it to 0x01 must NOT be accepted as a "version"; it makes newSize ~2^56, which
        // the patch cannot satisfy, so application fails instead of succeeding.
        var patchBytes = patchStream.ToArray();
        Assert.That(patchBytes[31], Is.EqualTo(0x00));
        patchBytes[31] = 0x01;

        using var outputStream = new MemoryStream();
        Assert.That(() => BSPatch.Apply(oldData, patchBytes, outputStream),
            Throws.InstanceOf<InvalidOperationException>().Or.InstanceOf<EndOfStreamException>());
    }

    [Test]
    public void WritePackedLong_SpanTooSmall_ThrowsArgumentException()
    {
        var small = new byte[7];
        Assert.Throws<ArgumentException>(() => SpanExtensions.WritePackedLong(small.AsSpan(), 42));
    }

    [Test]
    public void WritePackedLong_ExactlyEightBytes_Succeeds()
    {
        var exact = new byte[8];
        SpanExtensions.WritePackedLong(exact.AsSpan(), 42);
        Assert.That(SpanExtensions.ReadPackedLong((ReadOnlySpan<byte>)exact), Is.EqualTo(42));
    }

    private class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
