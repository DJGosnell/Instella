using Instella.Core.Trust;
using Instella.Core.Update;

namespace Instella.Installer.Runtime.Core.Update;

/// <summary>
/// Where the update engine gets bytes from. Nothing it returns is trusted: the engine
/// verifies the release signature and hashes every staged file against the release.
/// </summary>
internal interface IUpdateDownloader
{
    /// <summary>The target version's signed release, or null when the build was uploaded unsigned.</summary>
    Task<SignedRelease?> GetReleaseAsync(CancellationToken ct);

    /// <summary>Opens one file of the target build.</summary>
    Task<Stream> DownloadFileAsync(string relativePath, CancellationToken ct);

    /// <summary>The patch recipe from the installed to the target version, or null when none exists.</summary>
    Task<PatchManifest?> DownloadPatchManifestAsync(CancellationToken ct);

    /// <summary>
    /// Opens the patch blob named <c>{patchSha256}.patch</c> inside the patch archive (seekable).
    /// A blob larger than <paramref name="maxBytes"/> is refused.
    /// </summary>
    Task<Stream> OpenPatchEntryAsync(string patchSha256, long maxBytes, CancellationToken ct);

    /// <summary>
    /// The verified release's total file size and file count, once known: downloads of the patch
    /// archive and the full build are bounded by them.
    /// </summary>
    void SetReleaseSize(long totalBytes, int fileCount) { }

    /// <summary>
    /// The full build as a ZIP. Only used for installations built with
    /// <c>AllowUnsignedUpdates()</c> whose target has no signed release to list its files.
    /// </summary>
    Task<Stream> DownloadFullBuildAsync(CancellationToken ct);
}
