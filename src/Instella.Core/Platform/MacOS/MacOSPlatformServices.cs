using System.Diagnostics;
using System.Runtime.Versioning;

namespace Instella.Core.Platform.MacOS;

/// <summary>
/// macOS-specific platform services implementation.
/// Uses symbolic links for shortcuts, duti for file associations,
/// shell profiles for PATH, and LaunchAgents for auto-start.
/// </summary>
[SupportedOSPlatform("osx")]
internal sealed class MacOSPlatformServices : IPlatformServices
{
    public TargetPlatform Platform => TargetPlatform.MacOS;

    public string GetDefaultInstallPath(string appName, bool perUser)
    {
        if (perUser)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Applications", appName);
        }

        return Path.Combine("/Applications", appName);
    }

    /// <summary>
    /// Desktop shortcuts are symlinks to the executable. Start-menu shortcuts are not supported:
    /// the natural location, <c>~/Applications/{App}</c>, is also the default install directory,
    /// so a link there could overwrite the executable.
    /// </summary>
    public Task<PlatformResult> CreateShortcutAsync(ShortcutInfo info, CancellationToken ct)
    {
        if (info.Location != ShortcutLocation.Desktop)
            return Task.FromResult(PlatformResult.Fail("start-menu shortcuts are not supported on macOS"));
        try
        {
            var linkPath = DesktopLinkPath(info);
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            if (Directory.Exists(linkPath))
                return Task.FromResult(PlatformResult.Fail($"'{linkPath}' is a directory"));
            if (File.Exists(linkPath) || new FileInfo(linkPath).LinkTarget is not null)
            {
                if (new FileInfo(linkPath).LinkTarget is null)
                    return Task.FromResult(PlatformResult.Fail($"'{linkPath}' exists and is not a shortcut"));
                File.Delete(linkPath);
            }
            File.CreateSymbolicLink(linkPath, info.TargetPath);
            return Task.FromResult(PlatformResult.Ok);
        }
        catch (Exception ex)
        {
            return Task.FromResult(PlatformResult.Fail(ex.Message));
        }
    }

    /// <summary>Removes a desktop symlink, and only a symlink.</summary>
    public Task<PlatformResult> RemoveShortcutAsync(ShortcutInfo info, CancellationToken ct)
    {
        if (info.Location != ShortcutLocation.Desktop)
            return Task.FromResult(PlatformResult.Ok);   // never created (see CreateShortcutAsync)
        try
        {
            var linkPath = DesktopLinkPath(info);
            if (new FileInfo(linkPath).LinkTarget is not null)
                File.Delete(linkPath);
            return Task.FromResult(PlatformResult.Ok);
        }
        catch (Exception ex)
        {
            return Task.FromResult(PlatformResult.Fail(ex.Message));
        }
    }

    private static string DesktopLinkPath(ShortcutInfo info) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", info.Name);

    public async Task<PlatformResult> RegisterFileAssociationAsync(FileAssociationInfo info, CancellationToken ct)
    {
        try
        {
            // Use duti to set file associations (requires duti to be installed)
            var bundleId = GetBundleId(info.ExecutablePath, info.AppId);
            var result = await ExternalCommand.RunAsync("duti", ["-s", bundleId, info.Extension, "all"], ct);
            return result.Success ? result : PlatformResult.Fail($"could not register the association with duti: {result.Error}");
        }
        catch (Exception ex)
        {
            // File associations on macOS normally come from the app bundle's Info.plist;
            // this path needs the duti tool.
            return PlatformResult.Fail($"could not register the association with duti: {ex.Message}");
        }
    }

    public Task<PlatformResult> UnregisterFileAssociationAsync(string extension, string appId, CancellationToken ct)
    {
        // File association removal on macOS requires system preferences
        // There's no user-space API for this, so we just return success
        return Task.FromResult(PlatformResult.Ok);
    }

    public async Task<PlatformResult> AddToPathAsync(string directory, bool perUser, CancellationToken ct)
    {
        if (!perUser)
        {
            // System-wide PATH modification requires root
            return PlatformResult.Fail("system-wide PATH changes are not supported on macOS");
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
            return PlatformResult.Fail("system-wide PATH changes are not supported on macOS");

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
            var launchAgentsDir = Path.Combine(home, "Library", "LaunchAgents");
            Directory.CreateDirectory(launchAgentsDir);

            var label = $"com.instella.{SanitizeName(info.AppId)}";
            var plistPath = Path.Combine(launchAgentsDir, $"{label}.plist");

            var programArgs = string.IsNullOrEmpty(info.Arguments)
                ? $"<string>{Xml(info.ExecutablePath)}</string>"
                : $"<string>{Xml(info.ExecutablePath)}</string>\n                        <string>{Xml(info.Arguments)}</string>";

            var plistContent = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key>
                    <string>{Xml(label)}</string>
                    <key>ProgramArguments</key>
                    <array>
                        {programArgs}
                    </array>
                    <key>RunAtLoad</key>
                    <true/>
                </dict>
                </plist>
                """;

            await File.WriteAllTextAsync(plistPath, plistContent, ct);

            // Load the launch agent
            return await ExternalCommand.RunAsync("launchctl", ["load", plistPath], ct);
        }
        catch (Exception ex)
        {
            return PlatformResult.Fail(ex.Message);
        }
    }

    public async Task<PlatformResult> RemoveAutoStartAsync(string appId, CancellationToken ct)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var label = $"com.instella.{SanitizeName(appId)}";
            var plistPath = Path.Combine(home, "Library", "LaunchAgents", $"{label}.plist");

            if (!File.Exists(plistPath))
                return PlatformResult.Ok;

            // An agent that is not loaded fails to unload; either way the plist has to go.
            await ExternalCommand.RunAsync("launchctl", ["unload", plistPath], ct);
            File.Delete(plistPath);
            return PlatformResult.Ok;
        }
        catch (Exception ex)
        {
            return PlatformResult.Fail(ex.Message);
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
                // On macOS, use ps command to get process path
                var modulePath = GetProcessPath(process.Id);
                if (modulePath != null)
                {
                    var moduleDir = Path.GetDirectoryName(modulePath);
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
        // macOS uses zsh by default since Catalina
        var zshrc = Path.Combine(home, ".zshrc");
        if (File.Exists(zshrc)) return zshrc;

        var bashProfile = Path.Combine(home, ".bash_profile");
        if (File.Exists(bashProfile)) return bashProfile;

        // Default to .zshrc
        return zshrc;
    }

    private static string GetBundleId(string executablePath, string fallbackAppId)
    {
        // Try to extract bundle ID from app bundle
        var appPath = executablePath;
        if (executablePath.Contains(".app/", StringComparison.Ordinal))
        {
            appPath = executablePath[..(executablePath.IndexOf(".app/", StringComparison.Ordinal) + 4)];
        }

        var infoPlist = Path.Combine(appPath, "Contents", "Info.plist");
        if (File.Exists(infoPlist))
        {
            try
            {
                var content = File.ReadAllText(infoPlist);
                var bundleIdIndex = content.IndexOf("CFBundleIdentifier", StringComparison.Ordinal);
                if (bundleIdIndex >= 0)
                {
                    var stringStart = content.IndexOf("<string>", bundleIdIndex, StringComparison.Ordinal);
                    var stringEnd = content.IndexOf("</string>", stringStart, StringComparison.Ordinal);
                    if (stringStart >= 0 && stringEnd > stringStart)
                    {
                        return content[(stringStart + 8)..stringEnd];
                    }
                }
            }
            catch
            {
                // Fall through to default
            }
        }

        // Use the fallback app ID
        return $"com.instella.{SanitizeName(fallbackAppId)}";
    }

    private static string? GetProcessPath(int pid)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = "ps";
            process.StartInfo.ArgumentList.Add("-p");
            process.StartInfo.ArgumentList.Add(pid.ToString());
            process.StartInfo.ArgumentList.Add("-o");
            process.StartInfo.ArgumentList.Add("comm=");
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.CreateNoWindow = true;

            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            return string.IsNullOrEmpty(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    public Task<PlatformResult> UpdateShortcutAsync(string oldTarget, string newTarget, string shortcutPath, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(shortcutPath) && !Directory.Exists(shortcutPath))
                return Task.FromResult(PlatformResult.Fail($"shortcut '{shortcutPath}' does not exist"));

            // macOS shortcuts are symlinks — delete and recreate
            File.Delete(shortcutPath);
            File.CreateSymbolicLink(shortcutPath, newTarget);
            return Task.FromResult(PlatformResult.Ok);
        }
        catch (Exception ex)
        {
            return Task.FromResult(PlatformResult.Fail(ex.Message));
        }
    }

    // macOS has no Add/Remove Programs analogue; users drag .app bundles to
    // Trash. Treat these as successful no-ops.
    public Task<PlatformResult> RegisterUninstallEntryAsync(UninstallEntryInfo info, CancellationToken ct)
        => Task.FromResult(PlatformResult.Ok);

    public Task<PlatformResult> UnregisterUninstallEntryAsync(string appId, bool perUser, CancellationToken ct)
        => Task.FromResult(PlatformResult.Ok);

    private static string Xml(string value) => System.Security.SecurityElement.Escape(value);
}
