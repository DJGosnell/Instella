using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Utilities;
using Instella.Core.Logging;
using Instella.Core.Trust;
using Instella.Installer.Runtime.Builders;

namespace Instella.Installer.Runtime.Runners;

/// <summary>What the user chose when a newer version is available.</summary>
internal enum NewerVersionChoice
{
    /// <summary>Download the newer version's installer and run it.</summary>
    InstallNewer,

    /// <summary>Install the version this installer carries.</summary>
    InstallThis,

    /// <summary>Stop.</summary>
    Cancel,
}

/// <summary>The prompt shown when <paramref name="newer"/> is available.</summary>
internal delegate NewerVersionChoice NewerVersionPrompt(string appName, Version current, AvailableVersion newer);

/// <summary>
/// <c>WithNewerVersionPrompt()</c>: before an interactive install starts, ask the server whether
/// a newer version has an online installer and offer it. The newer version is installed by
/// handing over to its own installer (<see cref="InstallerHandoff"/>). Anything that goes wrong
/// on the way (offline, private package, unsigned or tampered release) is logged and the
/// install carries on with this installer's version; nothing is shown unless there is a choice.
/// </summary>
internal sealed class NewerVersionOffer
{
    /// <summary>How long the check may take before the install carries on without it.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(8);

    private readonly FrozenConfig _config;
    private readonly IInstellaLogger _log;
    private readonly NewerVersionPrompt _prompt;
    private readonly Action<string> _showError;

    public NewerVersionOffer(FrozenConfig config, IInstellaLogger log, NewerVersionPrompt? prompt = null, Action<string>? showError = null)
    {
        _config = config;
        _log = log;
        _prompt = prompt ?? MessageBoxPrompt;
        _showError = showError ?? MessageBoxError;
    }

    /// <summary>Test hook: reads Authenticode signatures; null uses WinVerifyTrust on Windows.</summary>
    internal Instella.Core.Platform.Windows.IAuthenticodeReader? Authenticode { get; init; }

    /// <summary>Test hook: the running installer's path, for its own signature.</summary>
    internal string? SelfPath { get; init; }

    /// <summary>Test hook: runs the downloaded installer; null starts it and waits (<see cref="InstallerHandoff.RunAsync"/>).</summary>
    internal Func<string, IReadOnlyList<string>, CancellationToken, Task<int>>? Launcher { get; init; }

    /// <summary>
    /// The version already installed, if any (<see cref="InstalledVersionProbe"/>): a newer version
    /// that is installed already, or older than the installed one, is not offered.
    /// </summary>
    internal Func<CancellationToken, Task<Version?>>? InstalledVersion { get; init; }

    /// <summary>
    /// Returns the exit code to end with (the newer installer's, or 1 when the user cancelled), or
    /// null to carry on installing this version.
    /// </summary>
    public async Task<int?> RunAsync(HttpClient http, IReadOnlyList<string> args, CancellationToken ct)
    {
        InstallerHandoff.SweepOldDownloads();

        VerifiedInstaller verified;
        AvailableVersion newer;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(CheckTimeout);
            try
            {
                var versions = await InstallerHandoff.ListAsync(_config, http, _config.Channel, timeout.Token);
                var candidate = versions.FirstOrDefault();
                if (candidate is null || AppVersions.Compare(candidate.Version, _config.AppVersion) <= 0)
                {
                    _log.Info($"newer-version check: {_config.AppVersion} is the newest version with an installer");
                    return null;
                }
                if (InstalledVersion is not null && await InstalledVersion(timeout.Token) is { } installed
                    && AppVersions.Compare(installed, candidate.Version) >= 0)
                {
                    _log.Info($"newer-version check: {installed} is installed already, not offering {candidate.Version}");
                    return null;
                }
                newer = candidate;
                verified = await InstallerHandoff.VerifyAsync(_config, http, newer.Version, _config.Channel, timeout.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or UpdateTrustException or System.Text.Json.JsonException
                                           or NotSupportedException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                _log.Info($"newer-version check skipped: {ex.Message}");
                return null;
            }
        }

        _log.Info($"newer-version check: {newer.Version} is available (this installer: {_config.AppVersion})");
        switch (_prompt(_config.AppName, _config.AppVersion, newer))
        {
            case NewerVersionChoice.Cancel:
                _log.Info("newer-version check: user cancelled");
                return (int)InstellaExitCode.UserCancelled;
            case NewerVersionChoice.InstallThis:
                _log.Info($"newer-version check: user chose to install {_config.AppVersion}");
                return null;
        }

        VerifiedInstallerFile file;
        try
        {
            file = await InstallerHandoff.DownloadAsync(_config, http, verified, ct, Authenticode, SelfPath, _log);
        }
        catch (InstallerSignerMismatchException ex)
        {
            // Not a download problem: a security refusal. Nothing is installed.
            _log.Error($"newer-version check: {ex.Message}");
            _showError($"The installer for {_config.AppName} {newer.Version} was refused: {ex.Message}.");
            return (int)InstellaExitCode.InstallIntegrityFailed;
        }
        catch (Exception ex) when (ex is HttpRequestException or UpdateTrustException or System.IO.IOException)
        {
            _log.Warn($"newer-version check: could not download the {newer.Version} installer: {ex.Message}");
            _showError($"The installer for {_config.AppName} {newer.Version} could not be downloaded:\n\n{ex.Message}\n\n" +
                       $"{_config.AppName} {_config.AppVersion} will be installed instead.");
            return null;
        }

        // The handle that keeps writers out is released only after the installer has run.
        await using (file)
        {
            _log.Info($"newer-version check: handing over to {file.Path}");
            return await (Launcher ?? InstallerHandoff.RunAsync)(file.Path, args, ct);
        }
    }

    private static NewerVersionChoice MessageBoxPrompt(string appName, Version current, AvailableVersion newer)
    {
        if (!OperatingSystem.IsWindows()) return NewerVersionChoice.InstallThis;
        var changelog = string.IsNullOrWhiteSpace(newer.Changelog) ? "" : "\n\nWhat's new:\n" + Shorten(newer.Changelog!.Trim(), 700);
        var text =
            $"{appName} {newer.Version} is available. This installer installs version {current}.{changelog}\n\n" +
            $"Yes: download and install {newer.Version} (recommended, {FormatSize(newer.InstallerSize)} download)\n" +
            $"No: install {current}\n" +
            "Cancel: exit";
        var result = UI.Windows.Win32.MessageBoxW(0, text, $"A newer {appName} is available",
            UI.Windows.MB.YESNOCANCEL | UI.Windows.MB.ICONQUESTION | UI.Windows.MB.TOPMOST);
        return result switch
        {
            UI.Windows.IDRESULT.YES => NewerVersionChoice.InstallNewer,
            UI.Windows.IDRESULT.NO => NewerVersionChoice.InstallThis,
            _ => NewerVersionChoice.Cancel,
        };
    }

    private static void MessageBoxError(string message) =>
        new UserMessages(silent: false).Error("Download failed", message);

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";

    private static string FormatSize(long bytes) => bytes >= 1_048_576 ? $"{bytes / 1_048_576.0:F1} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
