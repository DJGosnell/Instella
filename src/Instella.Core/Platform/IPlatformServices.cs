using System.Diagnostics;

namespace Instella.Core.Platform;

/// <summary>
/// Platform-specific services interface.
/// Abstracts OS-specific operations for shortcuts, file associations, PATH, and auto-start.
/// </summary>
public interface IPlatformServices
{
    /// <summary>The target platform this service is for.</summary>
    TargetPlatform Platform { get; }

    /// <summary>
    /// Gets the default installation path for an application on this platform.
    /// </summary>
    /// <param name="appName">Application name.</param>
    /// <param name="perUser">If true, returns per-user install path; otherwise system-wide.</param>
    /// <returns>The default installation directory path.</returns>
    string GetDefaultInstallPath(string appName, bool perUser);

    /// <summary>Creates a shortcut.</summary>
    /// <param name="info">Shortcut information.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success when the shortcut was created; otherwise the reason it failed.</returns>
    Task<PlatformResult> CreateShortcutAsync(ShortcutInfo info, CancellationToken ct);

    /// <summary>Removes a shortcut.</summary>
    /// <param name="info">Shortcut information (Name and Location must be set).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success when the shortcut was removed; otherwise the reason it failed.</returns>
    Task<PlatformResult> RemoveShortcutAsync(ShortcutInfo info, CancellationToken ct);

    /// <summary>Registers a file type association.</summary>
    /// <param name="info">File association information.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success when the association was registered; otherwise the reason it failed.</returns>
    Task<PlatformResult> RegisterFileAssociationAsync(FileAssociationInfo info, CancellationToken ct);

    /// <summary>Unregisters a file type association.</summary>
    /// <param name="extension">The file extension to unregister.</param>
    /// <param name="appId">The application ID that owns the association.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success when the association was unregistered; otherwise the reason it failed.</returns>
    Task<PlatformResult> UnregisterFileAssociationAsync(string extension, string appId, CancellationToken ct);

    /// <summary>Adds a directory to the user's PATH environment variable.</summary>
    /// <param name="directory">Directory to add.</param>
    /// <param name="perUser">If true, modifies user PATH; otherwise system PATH.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success when the directory was added; otherwise the reason it failed.</returns>
    Task<PlatformResult> AddToPathAsync(string directory, bool perUser, CancellationToken ct);

    /// <summary>Removes a directory from the PATH environment variable.</summary>
    /// <param name="directory">Directory to remove.</param>
    /// <param name="perUser">If true, modifies user PATH; otherwise system PATH.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success when the directory was removed; otherwise the reason it failed.</returns>
    Task<PlatformResult> RemoveFromPathAsync(string directory, bool perUser, CancellationToken ct);

    /// <summary>Configures an application to start automatically on login.</summary>
    /// <param name="info">Auto-start configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success when auto-start was configured; otherwise the reason it failed.</returns>
    Task<PlatformResult> ConfigureAutoStartAsync(AutoStartInfo info, CancellationToken ct);

    /// <summary>Removes auto-start configuration for an application.</summary>
    /// <param name="appId">The application ID to remove from auto-start.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success when auto-start was removed; otherwise the reason it failed.</returns>
    Task<PlatformResult> RemoveAutoStartAsync(string appId, CancellationToken ct);

    /// <summary>Gets processes running from a specific path with the given name.</summary>
    /// <param name="processName">Process name (without extension).</param>
    /// <param name="installPath">Optional install path to filter by.</param>
    /// <returns>List of matching processes.</returns>
    IReadOnlyList<Process> GetRunningProcesses(string processName, string? installPath);

    /// <summary>Attempts to terminate a process gracefully, then forcefully if needed.</summary>
    /// <param name="process">Process to terminate.</param>
    /// <param name="timeout">Time to wait for graceful termination before forcing.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success when the process was terminated; otherwise the reason it failed.</returns>
    Task<PlatformResult> TerminateProcessAsync(Process process, TimeSpan timeout, CancellationToken ct);

    /// <summary>Updates an existing shortcut to point to a new target path.</summary>
    /// <param name="oldTarget">The current target path of the shortcut.</param>
    /// <param name="newTarget">The new target path for the shortcut.</param>
    /// <param name="shortcutPath">Path to the shortcut file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success when the shortcut was updated; otherwise the reason it failed.</returns>
    Task<PlatformResult> UpdateShortcutAsync(string oldTarget, string newTarget, string shortcutPath, CancellationToken ct);

    /// <summary>Registers the application in the OS "Installed Apps" / Add-Remove Programs list.</summary>
    /// <param name="info">Uninstall entry information.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success when the entry was registered successfully (or if the platform has no such concept); otherwise the reason it failed.</returns>
    Task<PlatformResult> RegisterUninstallEntryAsync(UninstallEntryInfo info, CancellationToken ct);

    /// <summary>Removes a previously registered "Installed Apps" / Add-Remove Programs entry.</summary>
    /// <param name="appId">Application ID used during registration.</param>
    /// <param name="perUser">Must match the scope the entry was registered with.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success when the entry was removed (or did not exist); otherwise the reason it failed.</returns>
    Task<PlatformResult> UnregisterUninstallEntryAsync(string appId, bool perUser, CancellationToken ct);

    /// <summary>
    /// Deletes a single named value from a Windows registry key. Used by the
    /// tracked-rollback ledger to reverse registry writes performed by custom
    /// install steps. Linux/macOS succeed as a no-op so cross-platform
    /// rollback paths stay declarative.
    /// </summary>
    Task<PlatformResult> DeleteRegistryValueAsync(RegistryHive hive, string keyPath, string name, bool perUser, CancellationToken ct)
        => Task.FromResult(PlatformResult.Ok);

    /// <summary>
    /// Deletes a Windows registry key (recursively). Companion to
    /// <see cref="DeleteRegistryValueAsync"/> for tracked-rollback unwind of
    /// keys that the install created. Linux/macOS succeed as a no-op.
    /// </summary>
    Task<PlatformResult> DeleteRegistryKeyAsync(RegistryHive hive, string keyPath, bool perUser, CancellationToken ct)
        => Task.FromResult(PlatformResult.Ok);

    /// <summary>
    /// Writes a single named value into a Windows registry key, creating the
    /// key tree if necessary. The companion to <see cref="DeleteRegistryValueAsync"/>
    /// — the fluent <c>OnWindows.AddRegistryKey</c> sub-builder produces a
    /// list of writes, and the built-in <c>WriteRegistrySpecsStep</c> applies
    /// them via this method. Linux/macOS succeed as a no-op.
    /// </summary>
    Task<PlatformResult> WriteRegistryValueAsync(RegistryHive hive, string keyPath, string name, InstellaRegistryValueKind kind, object value, bool perUser, CancellationToken ct)
        => Task.FromResult(PlatformResult.Ok);

    /// <summary>
    /// Reads one named value, or null when it (or its key) does not exist. Upgrades use it
    /// to snapshot a value before overwriting it, so a failed upgrade can put it back.
    /// Linux/macOS return null (no registry).
    /// </summary>
    Task<RegistryValueData?> ReadRegistryValueAsync(RegistryHive hive, string keyPath, string name, bool perUser, CancellationToken ct)
        => Task.FromResult<RegistryValueData?>(null);

    /// <summary>
    /// True when the registry key exists. Install uses it to record which keys Instella
    /// created, so uninstall deletes only those. Linux/macOS return false.
    /// </summary>
    Task<bool> RegistryKeyExistsAsync(RegistryHive hive, string keyPath, bool perUser, CancellationToken ct)
        => Task.FromResult(false);

    /// <summary>
    /// Deletes the key only when it has no values and no subkeys (a key Instella created
    /// that something else has since written to is left alone). Succeeds when the key is gone;
    /// fails when it still holds values or subkeys. Linux/macOS succeed as a no-op.
    /// </summary>
    Task<PlatformResult> DeleteRegistryKeyIfEmptyAsync(RegistryHive hive, string keyPath, bool perUser, CancellationToken ct)
        => Task.FromResult(PlatformResult.Ok);

    /// <summary>
    /// Scope-aware <see cref="UnregisterFileAssociationAsync(string, string, CancellationToken)"/>:
    /// machine installs registered under <c>HKLM\Software\Classes</c>. The default ignores the
    /// scope, for implementations with a single scope.
    /// </summary>
    Task<PlatformResult> UnregisterFileAssociationAsync(string extension, string appId, bool perUser, CancellationToken ct)
        => UnregisterFileAssociationAsync(extension, appId, ct);

    /// <summary>
    /// Scope-aware <see cref="RemoveAutoStartAsync(string, CancellationToken)"/>: machine installs
    /// registered under <c>HKLM\…\Run</c>. The default ignores the scope.
    /// </summary>
    Task<PlatformResult> RemoveAutoStartAsync(string appId, bool perUser, CancellationToken ct)
        => RemoveAutoStartAsync(appId, ct);
}
