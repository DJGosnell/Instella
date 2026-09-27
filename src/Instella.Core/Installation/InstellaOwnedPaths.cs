namespace Instella.Core.Installation;

/// <summary>
/// Files in an install directory that belong to Instella rather than to the app payload:
/// the stub, the installed manifest, and the <c>.instella/</c> state folder (transaction
/// journal, backups, tombstones). Any "delete what the new version no longer contains"
/// logic filters through <see cref="IsOwned"/>, and a release may never list these paths.
/// </summary>
internal static class InstellaOwnedPaths
{
    /// <summary>The stub's file name on the current OS (<c>instella.exe</c> on Windows, <c>instella</c> elsewhere).</summary>
    public static string StubFileName => OperatingSystem.IsWindows() ? "instella.exe" : "instella";

    /// <summary>The installed manifest, at the install root.</summary>
    public const string InstalledManifest = ".instella-manifest.json";

    /// <summary>Instella's state folder, at the install root.</summary>
    public const string StateDirectory = ".instella";

    /// <summary>
    /// The app icon, embedded in the payload at build time so shortcuts, the ARP
    /// entry and file associations never point at a build-machine path.
    /// </summary>
    public const string AppIcon = StateDirectory + "/app.ico";

    /// <summary>
    /// Where uninstall moves app files it could not delete because a program still had them
    /// open; cleanup, or the next install into the folder, deletes them.
    /// </summary>
    public const string PendingDeleteDirectory = StateDirectory + "/pending-delete";

    /// <summary>Where the updater leaves post-update markers for the restarted app (<see cref="PostUpdateMarker"/>).</summary>
    public const string PostUpdateDirectory = StateDirectory + "/post-update";

    /// <summary>
    /// Where the payload carries the separately signed stub. It is staged as
    /// <see cref="StubFileName"/> at the install root, never extracted in place.
    /// </summary>
    public static string PayloadStub => StateDirectory + "/" + StubFileName;

    /// <summary>True when <paramref name="relativePath"/> (either separator) is Instella-owned.</summary>
    public static bool IsOwned(string relativePath)
    {
        var p = relativePath.Replace('\\', '/').TrimStart('/');
        return p.Equals("instella.exe", StringComparison.OrdinalIgnoreCase)
            || p.Equals("instella", StringComparison.OrdinalIgnoreCase)
            || p.Equals(InstalledManifest, StringComparison.OrdinalIgnoreCase)
            || p.Equals(StateDirectory, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(StateDirectory + "/", StringComparison.OrdinalIgnoreCase);
    }
}
