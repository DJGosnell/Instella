using System.Security.Cryptography;

namespace Instella.Core.Utilities;

/// <summary>
/// Checksum utilities for file verification.
/// </summary>
internal static class Checksum
{
    /// <summary>
    /// Computes SHA256 hash of a stream.
    /// </summary>
    public static async Task<string> ComputeSHA256Async(Stream stream, CancellationToken ct = default)
    {
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Computes SHA256 hash of a byte array.
    /// </summary>
    public static string ComputeSHA256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Computes SHA256 hash of a span.
    /// </summary>
    public static string ComputeSHA256(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Verifies that a stream matches the expected SHA256 hash.
    /// </summary>
    public static async Task<bool> VerifySHA256Async(Stream stream, string expectedHash, CancellationToken ct = default)
    {
        var actualHash = await ComputeSHA256Async(stream, ct);
        return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }
}
