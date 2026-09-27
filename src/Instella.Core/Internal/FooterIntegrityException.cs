namespace Instella.Core.Internal;

/// <summary>
/// Thrown when the embedded payload SHA256 hash does not match the computed hash.
/// </summary>
internal sealed class FooterIntegrityException : Exception
{
    public byte[] ExpectedHash { get; }
    public byte[] ActualHash { get; }

    /// <summary>A footer exists but is structurally broken (no hash to compare).</summary>
    public FooterIntegrityException(string message)
        : this(message, [], [])
    {
    }

    public FooterIntegrityException(string message, byte[] expectedHash, byte[] actualHash)
        : base(message)
    {
        ExpectedHash = expectedHash;
        ActualHash = actualHash;
    }
}
