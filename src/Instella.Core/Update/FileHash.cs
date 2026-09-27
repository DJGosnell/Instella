namespace Instella.Core.Update;

/// <summary>
/// Represents a file path and its expected SHA256 hash for verification.
/// </summary>
/// <param name="RelativePath">Path of the file relative to the install directory.</param>
/// <param name="Sha256">Expected SHA256 hash of the file content.</param>
internal sealed record FileHash(string RelativePath, string Sha256);
