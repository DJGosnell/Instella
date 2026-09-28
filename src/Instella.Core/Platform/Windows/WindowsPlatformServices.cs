using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Instella.Core.Platform.Windows;

/// <summary>
/// Windows-specific platform services implementation.
/// Handles shortcuts, file associations, PATH management, and auto-start configuration.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsPlatformServices : IPlatformServices
{
    private readonly WindowsShortcutCreator _shortcutCreator = new();

    public TargetPlatform Platform => TargetPlatform.Windows;

    public string GetDefaultInstallPath(string appName, bool perUser)
    {
        if (perUser)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Programs", appName);
        }

        // An x86 process on x64 Windows gets "Program Files (x86)" here (WOW64), as required.
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Path.Combine(programFiles, appName);
    }

    public Task<PlatformResult> CreateShortcutAsync(ShortcutInfo info, CancellationToken ct)
        => RunOnStaAsync(() =>
        {
            var shortcutPath = GetShortcutPath(info.Name, info.Location, info.PerUser);
            _shortcutCreator.Create(shortcutPath, info);
        });

    public Task<PlatformResult> RemoveShortcutAsync(ShortcutInfo info, CancellationToken ct)
        => RunOnStaAsync(() =>
        {
            var shortcutPath = GetShortcutPath(info.Name, info.Location, info.PerUser);
            _shortcutCreator.Delete(shortcutPath);
        });

    /// <summary>
    /// Runs <paramref name="action"/> on a dedicated STA-apartment thread,
    /// returning true if it completed without throwing. Required because
    /// <see cref="WindowsShortcutCreator"/> uses <c>IShellLinkW</c> via
    /// <c>CoCreateInstance</c> with CLSCTX_INPROC_SERVER — that interface
    /// is STA-threaded, and calling it from an MTA thread (the default for
    /// <c>Task.Run</c> work) marshals through a cross-apartment proxy that
    /// frequently hangs or returns failure HRESULTs. Symptom: shortcuts
    /// silently fail to be created. Running directly on an STA thread side-
    /// steps the proxy.
    /// </summary>
    private static Task<PlatformResult> RunOnStaAsync(Action action)
    {
        var tcs = new TaskCompletionSource<PlatformResult>();
        var thread = new Thread(() =>
        {
            try
            {
                action();
                tcs.SetResult(PlatformResult.Ok);
            }
            catch (Exception ex)
            {
                // Don't fail the install over a shortcut error, but DO leave
                // a breadcrumb in %TEMP%: otherwise a manifest that records a
                // shortcut with no .lnk on disk is impossible to diagnose.
                // One line per failure, append-only.
                LogShortcutFailure(ex);
                tcs.SetResult(PlatformResult.Fail(ex.Message));
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    private static void LogShortcutFailure(Exception ex)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "instella-shortcut.log");
            File.AppendAllText(path,
                $"{DateTime.UtcNow:O}  {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>Value under <c>Software\Classes\{ext}</c> naming the app id that created the key.</summary>
    internal const string OwnerValueName = "Instella.Owner";

    /// <summary>Value under <c>Software\Classes\{ext}</c> holding the default ProgID we replaced.</summary>
    internal const string PreviousDefaultValueName = "Instella.PreviousDefault";

    public Task<PlatformResult> RegisterFileAssociationAsync(FileAssociationInfo info, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            try
            {
                RegisterFileAssociation(info.PerUser ? Registry.CurrentUser : Registry.LocalMachine, info);
                NativeMethods.SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
                return PlatformResult.Ok;
            }
            catch (Exception ex)
            {
                return PlatformResult.Fail(ex.Message);
            }
        }, ct);
    }

    public Task<PlatformResult> UnregisterFileAssociationAsync(string extension, string appId, CancellationToken ct)
        => UnregisterFileAssociationAsync(extension, appId, perUser: true, ct);

    public Task<PlatformResult> UnregisterFileAssociationAsync(string extension, string appId, bool perUser, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            try
            {
                UnregisterFileAssociation(perUser ? Registry.CurrentUser : Registry.LocalMachine, extension, appId);
                NativeMethods.SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
                return PlatformResult.Ok;
            }
            catch (Exception ex)
            {
                return PlatformResult.Fail(ex.Message);
            }
        }, ct);
    }

    /// <summary>
    /// Registers our ProgID for an extension without destroying anyone else's: if the
    /// extension key already existed we record its previous default ProgID so uninstall
    /// can restore it, and only a key we created is marked as ours.
    /// </summary>
    internal static void RegisterFileAssociation(RegistryKey root, FileAssociationInfo info)
    {
        var progId = ProgIdFor(info.AppId, info.Extension);
        var extPath = $@"Software\Classes\{info.Extension}";

        using (var existing = root.OpenSubKey(extPath))
        {
            using var extKey = root.CreateSubKey(extPath)
                ?? throw new InvalidOperationException($"cannot create {extPath}");

            if (existing is null)
            {
                extKey.SetValue(OwnerValueName, info.AppId);
            }
            else if (extKey.GetValue(OwnerValueName) as string != info.AppId)
            {
                var previous = extKey.GetValue(null) as string;
                if (!string.IsNullOrEmpty(previous) && previous != progId && extKey.GetValue(PreviousDefaultValueName) is null)
                    extKey.SetValue(PreviousDefaultValueName, previous);
            }

            extKey.SetValue(null, progId);
            using var openWith = extKey.CreateSubKey("OpenWithProgids");
            openWith?.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using var progIdKey = root.CreateSubKey($@"Software\Classes\{progId}");
        progIdKey?.SetValue(null, info.Description);

        using var commandKey = root.CreateSubKey($@"Software\Classes\{progId}\shell\open\command");
        commandKey?.SetValue(null, $"\"{info.ExecutablePath}\" \"%1\"");

        if (!string.IsNullOrEmpty(info.IconPath))
        {
            using var iconKey = root.CreateSubKey($@"Software\Classes\{progId}\DefaultIcon");
            iconKey?.SetValue(null, info.IconPath);
        }
    }

    /// <summary>
    /// Deletes the extension key only if we created it; otherwise removes just our ProgID
    /// and <c>OpenWithProgids</c> entry and restores the default we replaced.
    /// </summary>
    internal static void UnregisterFileAssociation(RegistryKey root, string extension, string appId)
    {
        var progId = ProgIdFor(appId, extension);
        var extPath = $@"Software\Classes\{extension}";

        var ownedByUs = false;
        using (var extKey = root.OpenSubKey(extPath, writable: true))
        {
            if (extKey is not null)
            {
                ownedByUs = extKey.GetValue(OwnerValueName) as string == appId;
                if (!ownedByUs)
                {
                    using (var openWith = extKey.OpenSubKey("OpenWithProgids", writable: true))
                        openWith?.DeleteValue(progId, throwOnMissingValue: false);

                    if (extKey.GetValue(null) as string == progId)
                    {
                        if (extKey.GetValue(PreviousDefaultValueName) is string previous)
                            extKey.SetValue(null, previous);
                        else
                            extKey.DeleteValue("", throwOnMissingValue: false);
                    }
                    extKey.DeleteValue(PreviousDefaultValueName, throwOnMissingValue: false);
                }
            }
        }

        if (ownedByUs)
            root.DeleteSubKeyTree(extPath, throwOnMissingSubKey: false);
        root.DeleteSubKeyTree($@"Software\Classes\{progId}", throwOnMissingSubKey: false);
    }

    private static string ProgIdFor(string appId, string extension) => $"{appId}.{extension.TrimStart('.')}";

    public Task<PlatformResult> AddToPathAsync(string directory, bool perUser, CancellationToken ct) =>
        Task.Run(() => EditPath(perUser, current => PathListEditor.Add(current, directory)), ct);

    public Task<PlatformResult> RemoveFromPathAsync(string directory, bool perUser, CancellationToken ct) =>
        Task.Run(() => EditPath(perUser, current => PathListEditor.Remove(current, directory)), ct);

    /// <summary>
    /// Reads PATH <em>unexpanded</em>, applies <paramref name="edit"/>, and writes it back
    /// with the value kind it was read as. Reading with expansion and writing the result
    /// back would permanently replace <c>%JAVA_HOME%\bin</c> with a literal path.
    /// </summary>
    private static PlatformResult EditPath(bool perUser, Func<string, string?> edit)
    {
        try
        {
            var baseKey = perUser ? Registry.CurrentUser : Registry.LocalMachine;
            var keyPath = perUser ? "Environment" : @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

            using var envKey = baseKey.OpenSubKey(keyPath, writable: true);
            if (envKey == null) return PlatformResult.Fail($"registry key '{keyPath}' could not be opened for writing");

            var current = envKey.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
            var kind = envKey.GetValueNames().Contains("Path", StringComparer.OrdinalIgnoreCase)
                ? envKey.GetValueKind("Path")
                : RegistryValueKind.ExpandString;

            var updated = edit(current);
            if (updated is null) return PlatformResult.Ok;

            envKey.SetValue("Path", updated, kind is RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
            BroadcastSettingChange();
            return PlatformResult.Ok;
        }
        catch (Exception ex)
        {
            return PlatformResult.Fail(ex.Message);
        }
    }

    public Task<PlatformResult> ConfigureAutoStartAsync(AutoStartInfo info, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            try
            {
                using var runKey = (info.PerUser ? Registry.CurrentUser : Registry.LocalMachine)
                    .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                var value = string.IsNullOrEmpty(info.Arguments)
                    ? $"\"{info.ExecutablePath}\""
                    : $"\"{info.ExecutablePath}\" {info.Arguments}";
                runKey?.SetValue(info.AppId, value);
                return PlatformResult.Ok;
            }
            catch (Exception ex)
            {
                return PlatformResult.Fail(ex.Message);
            }
        }, ct);
    }

    public Task<PlatformResult> RemoveAutoStartAsync(string appId, CancellationToken ct)
        => RemoveAutoStartAsync(appId, perUser: true, ct);

    public Task<PlatformResult> RemoveAutoStartAsync(string appId, bool perUser, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            try
            {
                using var runKey = (perUser ? Registry.CurrentUser : Registry.LocalMachine)
                    .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                runKey?.DeleteValue(appId, throwOnMissingValue: false);
                return PlatformResult.Ok;
            }
            catch (Exception ex)
            {
                return PlatformResult.Fail(ex.Message);
            }
        }, ct);
    }

    public IReadOnlyList<Process> GetRunningProcesses(string processName, string? installPath)
    {
        var processes = Process.GetProcessesByName(processName);

        if (string.IsNullOrEmpty(installPath))
            return processes;

        var result = new List<Process>();

        foreach (var process in processes)
        {
            try
            {
                var modulePath = process.MainModule?.FileName;
                if (modulePath != null && IsUnderDirectory(modulePath, installPath))
                    result.Add(process);
                else
                    process.Dispose();
            }
            catch
            {
                process.Dispose();
            }
        }

        return result;
    }

    /// <summary>
    /// True when <paramref name="path"/> lies inside <paramref name="directory"/>. Compares
    /// against the directory plus a trailing separator, so <c>C:\App</c> does not claim
    /// <c>C:\AppData\x.exe</c>.
    /// </summary>
    internal static bool IsUnderDirectory(string path, string directory)
    {
        var root = Path.GetFullPath(directory);
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<PlatformResult> TerminateProcessAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            // Try graceful close first
            if (process.CloseMainWindow())
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);

                try
                {
                    await process.WaitForExitAsync(cts.Token);
                    return PlatformResult.Ok;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout, try force kill
                }
            }

            // Force kill
            process.Kill();
            await process.WaitForExitAsync(ct);
            return PlatformResult.Ok;
        }
        catch (Exception ex)
        {
            return PlatformResult.Fail(ex.Message);
        }
    }

    public Task<PlatformResult> RegisterUninstallEntryAsync(UninstallEntryInfo info, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            try
            {
                var baseKey = info.PerUser ? Registry.CurrentUser : Registry.LocalMachine;
                using var key = baseKey.CreateSubKey(UninstallEntryKeys.PathFor(info.AppId));
                if (key is null) return PlatformResult.Fail($"could not create the uninstall key for '{info.AppId}'");

                key.SetValue("DisplayName", info.DisplayName);
                key.SetValue("DisplayVersion", info.DisplayVersion);
                key.SetValue("Publisher", info.Publisher);
                key.SetValue("InstallLocation", info.InstallLocation);
                key.SetValue("DisplayIcon", info.DisplayIcon);
                key.SetValue("UninstallString", info.UninstallCommand);
                key.SetValue("QuietUninstallString", info.UninstallCommand + " --silent");
                if (!string.IsNullOrEmpty(info.UrlInfoAbout))
                    key.SetValue("URLInfoAbout", info.UrlInfoAbout);
                key.SetValue("EstimatedSize", info.EstimatedSizeKb, RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));

                return PlatformResult.Ok;
            }
            catch (Exception ex)
            {
                return PlatformResult.Fail(ex.Message);
            }
        }, ct);
    }

    public Task<PlatformResult> UnregisterUninstallEntryAsync(string appId, bool perUser, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            try
            {
                var baseKey = perUser ? Registry.CurrentUser : Registry.LocalMachine;
                baseKey.DeleteSubKeyTree(
                    UninstallEntryKeys.PathFor(appId),
                    throwOnMissingSubKey: false);
                return PlatformResult.Ok;
            }
            catch (Exception ex)
            {
                return PlatformResult.Fail(ex.Message);
            }
        }, ct);
    }

    public Task<PlatformResult> DeleteRegistryValueAsync(RegistryHive hive, string keyPath, string name, bool perUser, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            try
            {
                var baseKey = ResolveBaseKey(hive, perUser);
                using var key = baseKey.OpenSubKey(keyPath, writable: true);
                if (key is null) return PlatformResult.Ok; // key already gone — treat as success
                key.DeleteValue(name, throwOnMissingValue: false);
                return PlatformResult.Ok;
            }
            catch (Exception ex)
            {
                return PlatformResult.Fail(ex.Message);
            }
        }, ct);
    }

    public Task<PlatformResult> DeleteRegistryKeyAsync(RegistryHive hive, string keyPath, bool perUser, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            try
            {
                var baseKey = ResolveBaseKey(hive, perUser);
                baseKey.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
                return PlatformResult.Ok;
            }
            catch (Exception ex)
            {
                return PlatformResult.Fail(ex.Message);
            }
        }, ct);
    }

    public Task<PlatformResult> WriteRegistryValueAsync(RegistryHive hive, string keyPath, string name, InstellaRegistryValueKind kind, object value, bool perUser, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            try
            {
                var baseKey = ResolveBaseKey(hive, perUser);
                using var key = baseKey.CreateSubKey(keyPath, writable: true)
                    ?? throw new InvalidOperationException($"Could not create registry key {keyPath}");
                key.SetValue(name, value, MapKind(kind));
                return PlatformResult.Ok;
            }
            catch (Exception ex)
            {
                return PlatformResult.Fail(ex.Message);
            }
        }, ct);
    }

    public async Task<RegistryValueData?> ReadRegistryValueAsync(RegistryHive hive, string keyPath, string name, bool perUser, CancellationToken ct) =>
        (await TryReadRegistryValueAsync(hive, keyPath, name, perUser, ct)).Value;

    public Task<RegistryReadResult> TryReadRegistryValueAsync(RegistryHive hive, string keyPath, string name, bool perUser, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            try
            {
                using var key = ResolveBaseKey(hive, perUser).OpenSubKey(keyPath, writable: false);
                var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (key is null || value is null) return new RegistryReadResult(null, null);
                var kind = key.GetValueKind(name) switch
                {
                    RegistryValueKind.ExpandString => InstellaRegistryValueKind.ExpandString,
                    RegistryValueKind.Binary => InstellaRegistryValueKind.Binary,
                    RegistryValueKind.DWord => InstellaRegistryValueKind.DWord,
                    RegistryValueKind.MultiString => InstellaRegistryValueKind.MultiString,
                    RegistryValueKind.QWord => InstellaRegistryValueKind.QWord,
                    _ => InstellaRegistryValueKind.String,
                };
                return new RegistryReadResult(new RegistryValueData(kind, value), null);
            }
            catch (Exception ex)
            {
                // Access denied, a key deleted while reading, …: the value's existence is unknown.
                return new RegistryReadResult(null, ex.Message);
            }
        }, ct);
    }

    public Task<bool> RegistryKeyExistsAsync(RegistryHive hive, string keyPath, bool perUser, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            try
            {
                using var key = ResolveBaseKey(hive, perUser).OpenSubKey(keyPath, writable: false);
                return key is not null;
            }
            catch
            {
                return false;
            }
        }, ct);
    }

    public Task<PlatformResult> DeleteRegistryKeyIfEmptyAsync(RegistryHive hive, string keyPath, bool perUser, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            try
            {
                var baseKey = ResolveBaseKey(hive, perUser);
                using (var key = baseKey.OpenSubKey(keyPath, writable: false))
                {
                    if (key is null) return PlatformResult.Ok;
                    if (key.ValueCount > 0 || key.SubKeyCount > 0) return PlatformResult.Fail($"'{keyPath}' is not empty");
                }
                baseKey.DeleteSubKey(keyPath, throwOnMissingSubKey: false);
                return PlatformResult.Ok;
            }
            catch (Exception ex)
            {
                return PlatformResult.Fail(ex.Message);
            }
        }, ct);
    }

    private static RegistryValueKind MapKind(InstellaRegistryValueKind k) => k switch
    {
        InstellaRegistryValueKind.String => RegistryValueKind.String,
        InstellaRegistryValueKind.ExpandString => RegistryValueKind.ExpandString,
        InstellaRegistryValueKind.Binary => RegistryValueKind.Binary,
        InstellaRegistryValueKind.DWord => RegistryValueKind.DWord,
        InstellaRegistryValueKind.MultiString => RegistryValueKind.MultiString,
        InstellaRegistryValueKind.QWord => RegistryValueKind.QWord,
        _ => RegistryValueKind.String,
    };

    private static RegistryKey ResolveBaseKey(RegistryHive hive, bool perUser) => hive switch
    {
        RegistryHive.CurrentUser => Registry.CurrentUser,
        RegistryHive.LocalMachine => Registry.LocalMachine,
        RegistryHive.AutoFromScope => perUser ? Registry.CurrentUser : Registry.LocalMachine,
        _ => perUser ? Registry.CurrentUser : Registry.LocalMachine,
    };

    public Task<PlatformResult> UpdateShortcutAsync(string oldTarget, string newTarget, string shortcutPath, CancellationToken ct)
    {
        if (!File.Exists(shortcutPath))
            return Task.FromResult(PlatformResult.Fail($"shortcut '{shortcutPath}' does not exist"));

        // STA required — IPersistFile.Load + IShellLinkW.SetPath go through
        // the same COM apartment trap as Create/Delete.
        return RunOnStaAsync(() => _shortcutCreator.UpdateTarget(shortcutPath, newTarget));
    }

    private static string GetShortcutPath(string name, ShortcutLocation location, bool perUser)
    {
        var folder = (location, perUser) switch
        {
            (ShortcutLocation.Desktop, true) => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            (ShortcutLocation.Desktop, false) => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            (ShortcutLocation.StartMenu, true) => Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            (ShortcutLocation.StartMenu, false) => Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            _ => throw new ArgumentOutOfRangeException(nameof(location))
        };

        return Path.Combine(folder, $"{name}.lnk");
    }

    private static void BroadcastSettingChange()
    {
        NativeMethods.SendMessageTimeout(
            HWND_BROADCAST, WM_SETTINGCHANGE,
            IntPtr.Zero, "Environment",
            SMTO_ABORTIFHUNG, 5000, out _);
    }

    private const int HWND_BROADCAST = 0xFFFF;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int SMTO_ABORTIFHUNG = 0x0002;
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const int SHCNF_IDLIST = 0x0000;

    private static partial class NativeMethods
    {
        [LibraryImport("shell32.dll")]
        public static partial void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

        [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial IntPtr SendMessageTimeout(
            IntPtr hWnd,
            int msg,
            IntPtr wParam,
            string lParam,
            int fuFlags,
            int uTimeout,
            out IntPtr lpdwResult);
    }
}
