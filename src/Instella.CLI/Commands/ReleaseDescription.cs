using Instella.Core.Trust;
using Instella.Core.Wire;

namespace Instella.CLI.Commands;

/// <summary>
/// What <c>publish</c>, <c>approve</c> and <c>reject</c> show about a release before asking, and how the
/// commands report the state the server gave a release.
/// </summary>
internal static class ReleaseDescription
{
    /// <summary>The manifest summary: identity, files, installers, key rotation, the changelog's first line.</summary>
    public static void Write(string title, ReleaseManifest manifest, DateTime uploadedAt, string? changelog, TextWriter output)
    {
        output.WriteLine($"{title} {manifest.AppId} {manifest.Version} {manifest.Os}/{manifest.Arch}, channel {manifest.Channel}");
        output.WriteLine($"  Uploaded:  {uploadedAt:u}");
        output.WriteLine($"  Files:     {manifest.Files.Count} ({UploadCommand.FormatSize(manifest.Files.Sum(f => f.Size))})");
        foreach (var i in manifest.Installers ?? [])
            output.WriteLine($"  Installer: {i.Kind} {i.FileName} ({UploadCommand.FormatSize(i.Size)}, sha256 {i.Sha256})");
        if (manifest.TrustedKeys is { } rotate)
            output.WriteLine($"  Rotates trusted keys to: {string.Join(", ", rotate.Select(k => k.KeyId))}");
        if (!string.IsNullOrWhiteSpace(changelog))
            output.WriteLine($"  Changelog: {changelog.Trim().Split('\n')[0].TrimEnd('\r')}");
    }

    /// <summary>
    /// The line for a release the server holds back (<see cref="ReleaseStates.Pending"/>), or null when it
    /// is published or the server did not say.
    /// </summary>
    public static string? PendingMessage(string? state, DateTime? publishAfter) =>
        state != ReleaseStates.Pending ? null
        : publishAfter is { } at
            ? $"Pending: the server publishes it automatically at {at:u} unless someone rejects it first (release approval Delayed)."
            : "Pending approval: installations do not see it until someone approves it in the admin UI or with 'instella approve'.";
}
