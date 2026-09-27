namespace Instella.Core.Update;

/// <summary>
/// Represents a new file that needs to be downloaded in full (not present in previous version).
/// </summary>
/// <param name="RelativePath">Path of the file relative to the install directory.</param>
/// <param name="Sha256">SHA256 hash of the file content.</param>
/// <param name="Size">Size of the file in bytes.</param>
internal sealed record NewFile(string RelativePath, string Sha256, long Size);
