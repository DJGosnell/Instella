namespace Instella.Core.Update;

/// <summary>
/// Represents a file that has been modified and has a binary diff patch available.
/// </summary>
/// <param name="RelativePath">Path of the file relative to the install directory.</param>
/// <param name="PatchSha256">SHA256 hash of the patch data.</param>
/// <param name="PatchSize">Size of the patch data in bytes.</param>
/// <param name="ExpectedSha256">SHA256 hash the file should have after patching.</param>
internal sealed record PatchedFile(
    string RelativePath,
    string PatchSha256,
    long PatchSize,
    string ExpectedSha256);
