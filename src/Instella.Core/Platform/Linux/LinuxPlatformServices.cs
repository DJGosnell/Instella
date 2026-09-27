using System.Diagnostics;
using System.Runtime.Versioning;

namespace Instella.Core.Platform.Linux;

/// <summary>
/// Linux-specific platform services implementation (experimental).
/// Uses freedesktop.org specifications for .desktop files, MIME types, and autostart.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxPlatformServices : IPlatformServices
{
    public TargetPlatform Platform => TargetPlatform.Linux;

    public string GetDefaultInstallPath(string appName, bool perUser)
    {
        var normalizedName = SanitizeName(appName);

        if (perUser)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".local", "share", normalizedName);
        }

        return Path.Combine("/opt", normalizedName);
    }

    public async Task<PlatformResult> CreateShortcutAsync(ShortcutInfo info, CancellationToken ct)
    {
        try
        {
            var desktopFile = ShortcutPath(info);
            Directory.CreateDirectory(Path.GetDirectoryName(desktopFile)!);
            var content = $"""
                [Desktop Entry]
                Type=Application
                Name={DesktopEntry.EscapeString(info.Name)}
                Exec={DesktopEntry.Exec(info.TargetPath, info.Arguments)}
                Path={DesktopEntry.EscapeString(info.WorkingDirectory ?? Path.GetDirectoryName(info.TargetPath) ?? "")}
                Icon={DesktopEntry.EscapeString(info.IconPath ?? "")}
                Comment={DesktopEntry.EscapeString(info.Description ?? "")}
                Terminal=false
                Categories=Utility;
                """;

            await File.WriteAllTextAsync(desktopFile, content.Trim() + "\n", ct);

            // Desktop environments only launch .desktop files that are executable.
            File.SetUnixFileMode(desktopFile,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            return PlatformResult.Ok;
        }
        catch (Exception ex)
        {
            return PlatformResult.Fail(ex.Message);
        }
    }

    public Task<PlatformResult> RemoveShortcutAsync(ShortcutInfo info, CancellationToken ct)
    {
        try
        {
            var desktopFile = ShortcutPath(info);
            if (File.Exists(desktopFile))
                File.Delete(desktopFile);

            return Task.FromResult(PlatformResult.Ok);
        }
        catch (Exception ex)
        {
            return Task.FromResult(PlatformResult.Fail(ex.Message));
        }
    }

    private static string ShortcutPath(ShortcutInfo info)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directory = info.Location switch
        {
            ShortcutLocation.Desktop => Path.Combine(home, "Desktop"),
            ShortcutLocation.StartMenu => ApplicationsDirectory(),
            _ => throw new ArgumentOutOfRangeException(nameof(info))
        };
        return Path.Combine(directory, $"{SanitizeName(info.Name)}.desktop");
    }

    /// <summary>
    /// Registers a MIME type for the extension and a hidden <c>.desktop</c> entry that handles
    /// it (xdg-mime can only name a handler that exists as a desktop file), then makes that
    /// entry the default.
    /// </summary>
    public async Task<PlatformResult> RegisterFileAssociationAsync(FileAssociationInfo info, CancellationToken ct)
    {
        try
        {
            var extension = info.Extension.TrimStart('.');
            var mimeType = MimeTypeFor(extension);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var mimeRoot = Path.Combine(home, ".local", "share", "mime");

            var mimeDir = Path.Combine(mimeRoot, "packages");
            Directory.CreateDirectory(mimeDir);
            var mimeContent = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
                    <mime-type type="{mimeType}">
                        <comment>{System.Security.SecurityElement.Escape(info.Description)}</comment>
                        <glob pattern="*.{System.Security.SecurityElement.Escape(extension)}"/>
                    </mime-type>
                </mime-info>
                """;
            await File.WriteAllTextAsync(Path.Combine(mimeDir, MimePackageName(info.AppId, extension)), mimeContent, ct);

            var handler = Path.Combine(ApplicationsDirectory(), HandlerDesktopId(info.AppId, extension));
            Directory.CreateDirectory(Path.GetDirectoryName(handler)!);
            var handlerContent = $"""
                [Desktop Entry]
                Type=Application
                Name={DesktopEntry.EscapeString(info.Description)}
                Exec={DesktopEntry.Exec(info.ExecutablePath, fieldCode: "%f")}
                Icon={DesktopEntry.EscapeString(info.IconPath ?? "")}
                MimeType={mimeType};
                NoDisplay=true
                """;
            await File.WriteAllTextAsync(handler, handlerContent.Trim() + "\n", ct);

            var update = await ExternalCommand.RunAsync("update-mime-database", [mimeRoot], ct);
            if (!update.Success)
                return update;
            return await ExternalCommand.RunAsync("xdg-mime", ["default", HandlerDesktopId(info.AppId, extension), mimeType], ct);
        }
        catch (Exception ex)
        {
            return PlatformResult.Fail(ex.Message);
        }
    }

    public async Task<PlatformResult> UnregisterFileAssociationAsync(string extension, string appId, CancellationToken ct)
    {
        try
        {
            var ext = extension.TrimStart('.');
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var mimeRoot = Path.Combine(home, ".local", "share", "mime");

            var handler = Path.Combine(ApplicationsDirectory(), HandlerDesktopId(appId, ext));
            if (File.Exists(handler))
                File.Delete(handler);

            var mimeFile = Path.Combine(mimeRoot, "packages", MimePackageName(appId, ext));
            if (!File.Exists(mimeFile))
                return PlatformResult.Ok;
            File.Delete(mimeFile);
            return await ExternalCommand.RunAsync("update-mime-database", [mimeRoot], ct);
        }
        catch (Exception ex)
        {
            return PlatformResult.Fail(ex.Message);
        }
    }

    private static string ApplicationsDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "applications");

    private static string MimeTypeFor(string extension) => $"application/x-{extension}";

    private static string MimePackageName(string appId, string extension) => $"{appId}-{extension}.xml";

    /// <summary>The desktop-file id of the hidden handler registered for one extension.</summary>
    internal static string HandlerDesktopId(string appId, string extension) =>
        $"{SanitizeName(appId)}-{SanitizeName(extension.TrimStart('.'))}.desktop";

    public async Task<PlatformResult> AddToPathAsync(string directory, bool perUser, CancellationToken ct)
    {
        if (!perUser)
        {
            // System-wide PATH modification requires root and editing /etc/profile.d/
            return PlatformResult.Fail("system-wide PATH changes are not supported on Linux");
        }

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var profilePath = GetShellProfilePath(home);
            var marker = $"# Added by Instella for {directory}";
            var exportLine = $"\n{marker}\nexport PATH=\"{directory}:$PATH\"\n";

            var content = File.Exists(profilePath) ? await File.ReadAllTextAsync(profilePath, ct) : "";

            if (content.Contains(directory))
                return PlatformResult.Ok; // Already in PATH

            await File.AppendAllTextAsync(profilePath, exportLine, ct);
            return PlatformResult.Ok;
        }
        catch (Exception ex)
        {
            return PlatformResult.Fail(ex.Message);
        }
    }

    public async Task<PlatformResult> RemoveFromPathAsync(string directory, bool perUser, CancellationToken ct)
    {
        if (!perUser)
            return PlatformResult.Fail("system-wide PATH changes are not supported on Linux");

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var profilePath = GetShellProfilePath(home);

            if (!File.Exists(profilePath))
                return PlatformResult.Ok;

            var content = await File.ReadAllTextAsync(profilePath, ct);
            var marker = $"# Added by Instella for {directory}";
            var exportLine = $"export PATH=\"{directory}:$PATH\"";

            // Remove both the marker line and the export line
            content = content.Replace($"\n{marker}\n{exportLine}\n", "\n");
            content = content.Replace(exportLine, "");

            await File.WriteAllTextAsync(profilePath, content, ct);
            return PlatformResult.Ok;
        }
        catch (Exception ex)
        {
            return PlatformResult.Fail(ex.Message);
        }
    }

    public async Task<PlatformResult> ConfigureAutoStartAsync(AutoStartInfo info, CancellationToken ct)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var autostartDir = Path.Combine(home, ".config", "autostart");
            Directory.CreateDirectory(autostartDir);

            var desktopFile = Path.Combine(autostartDir, $"{SanitizeName(info.AppId)}.desktop");
            var content = $"""
                [Desktop Entry]
                Type=Application
                Name={DesktopEntry.EscapeString(info.AppId)}
                Exec={DesktopEntry.Exec(info.ExecutablePath, info.Arguments)}
                X-GNOME-Autostart-enabled=true
                """;

            await File.WriteAllTextAsync(desktopFile, content.Trim() + "\n", ct);
            return PlatformResult.Ok;
        }
        catch (Exception ex)
        {
            return PlatformResult.Fail(ex.Message);
        }
    }

    public Task<PlatformResult> RemoveAutoStartAsync(string appId, CancellationToken ct)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var desktopFile = Path.Combine(home, ".config", "autostart", $"{SanitizeName(appId)}.desktop");

            if (File.Exists(desktopFile))
                File.Delete(desktopFile);

            return Task.FromResult(PlatformResult.Ok);
        }
        catch (Exception ex)
        {
            return Task.FromResult(PlatformResult.Fail(ex.Message));
        }
    }

    public IReadOnlyList<Process> GetRunningProcesses(string processName, string? installPath)
    {
        var processes = Process.GetProcessesByName(processName);

        if (string.IsNullOrEmpty(installPath))
            return processes;

        var result = new List<Process>();
        var normalizedInstallPath = Path.GetFullPath(installPath).TrimEnd('/');

        foreach (var process in processes)
        {
            try
            {
                // On Linux, read /proc/{pid}/exe symlink to get actual path
                var exeLink = $"/proc/{process.Id}/exe";
                if (File.Exists(exeLink))
                {
                    var actualPath = Path.GetFullPath(new FileInfo(exeLink).LinkTarget ?? "");
                    var moduleDir = Path.GetDirectoryName(actualPath);

                    if (moduleDir != null && moduleDir.StartsWith(normalizedInstallPath, StringComparison.Ordinal))
                        result.Add(process);
                    else
                        process.Dispose();
                }
                else
                {
                    process.Dispose();
                }
            }
            catch
            {
                process.Dispose();
            }
        }

        return result;
    }

    public async Task<PlatformResult> TerminateProcessAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            // Send SIGTERM for graceful shutdown
            var term = await ExternalCommand.RunAsync("kill", ["-TERM", process.Id.ToString()], ct);
            if (!term.Success && !process.HasExited)
                return term;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(cts.Token);
                return PlatformResult.Ok;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout, send SIGKILL
                var kill = await ExternalCommand.RunAsync("kill", ["-KILL", process.Id.ToString()], ct);
                if (!kill.Success && !process.HasExited)
                    return kill;
                await process.WaitForExitAsync(ct);
                return PlatformResult.Ok;
            }
        }
        catch (Exception ex)
        {
            return PlatformResult.Fail(ex.Message);
        }
    }

    private static string SanitizeName(string name) =>
        name.ToLowerInvariant().Replace(' ', '-').Replace('_', '-');

    private static string GetShellProfilePath(string home)
    {
        // Check for zsh first (becoming more common)
        var zshrc = Path.Combine(home, ".zshrc");
        if (File.Exists(zshrc)) return zshrc;

        // Then bash
        var bashrc = Path.Combine(home, ".bashrc");
        if (File.Exists(bashrc)) return bashrc;

        // POSIX profile
        var profile = Path.Combine(home, ".profile");
        if (File.Exists(profile)) return profile;

        // Default to .profile
        return profile;
    }

    /// <summary>Points a <c>.desktop</c> shortcut at <paramref name="newTarget"/> by rewriting its <c>Exec=</c> executable.</summary>
    public async Task<PlatformResult> UpdateShortcutAsync(string oldTarget, string newTarget, string shortcutPath, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(shortcutPath))
                return PlatformResult.Fail($"shortcut '{shortcutPath}' does not exist");

            var content = await File.ReadAllTextAsync(shortcutPath, ct);
            var updated = content.Replace(DesktopEntry.QuoteArgument(oldTarget), DesktopEntry.QuoteArgument(newTarget), StringComparison.Ordinal);
            if (updated == content)
                return PlatformResult.Fail($"shortcut '{shortcutPath}' does not point at '{oldTarget}'");
            await File.WriteAllTextAsync(shortcutPath, updated, ct);
            return PlatformResult.Ok;
        }
        catch (Exception ex)
        {
            return PlatformResult.Fail(ex.Message);
        }
    }

    // Linux has no centralized "Installed Apps" registry; distro package managers
    // handle it out-of-band. Treat these as successful no-ops.
    public Task<PlatformResult> RegisterUninstallEntryAsync(UninstallEntryInfo info, CancellationToken ct)
        => Task.FromResult(PlatformResult.Ok);

    public Task<PlatformResult> UnregisterUninstallEntryAsync(string appId, bool perUser, CancellationToken ct)
        => Task.FromResult(PlatformResult.Ok);
}
