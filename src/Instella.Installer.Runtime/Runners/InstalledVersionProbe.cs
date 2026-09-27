using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Utilities;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;

namespace Instella.Installer.Runtime.Runners;

/// <summary>An installation of this app found on the machine.</summary>
/// <param name="Path">Its folder (normalised).</param>
/// <param name="Scope">Per user or machine-wide, from its installed manifest.</param>
/// <param name="Version">The installed version.</param>
/// <param name="Source">Where it was found: HKCU, HKLM, a default folder, or --path.</param>
internal sealed record ExistingInstallation(string Path, InstallationScope Scope, Version Version, string Source);

/// <summary>
/// Finds installations of this app before the scope (and so the install path) is chosen: the
/// folders the Installed Apps entries (per user, then per machine) point at, then the two default
/// folders. A candidate counts only when its installed manifest reads and names this app.
/// Read-only; anything unreadable counts as not installed.
/// </summary>
internal static class InstalledVersionProbe
{
    /// <summary>Every installation of this app, de-duplicated by folder, in the order above.</summary>
    public static async Task<IReadOnlyList<ExistingInstallation>> FindInstallationsAsync(
        FrozenConfig config, IPlatformServices platform, IFileSystem fs, CancellationToken ct)
    {
        var candidates = new List<(string Path, string Source)>();
        foreach (var (hive, perUser, source) in new[] { (RegistryHive.CurrentUser, true, "HKCU"), (RegistryHive.LocalMachine, false, "HKLM") })
        {
            try
            {
                var location = await platform.ReadRegistryValueAsync(
                    hive, UninstallEntryKeys.PathFor(config.AppId), "InstallLocation", perUser, ct);
                if (location?.Value is string path && path.Length > 0) candidates.Add((path, source));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An unreadable entry is not an installation.
            }
        }
        candidates.Add((InstallPaths.Default(config, platform, InstallationScope.PerUser), "default per-user folder"));
        candidates.Add((InstallPaths.Default(config, platform, InstallationScope.SystemWide), "default machine folder"));

        var found = new List<ExistingInstallation>();
        foreach (var (raw, source) in candidates)
        {
            if (!InstallPaths.TryNormalize(raw, requireRooted: true, out var path, out _)) continue;
            if (found.Any(f => InstallPaths.SameFolder(f.Path, path))) continue;
            if (await ReadAtAsync(config, path, source, fs, ct) is { } installation)
                found.Add(installation);
        }
        return found;
    }

    /// <summary>The installation of this app in <paramref name="path"/>, or null.</summary>
    public static async Task<ExistingInstallation?> ReadAtAsync(
        FrozenConfig config, string path, string source, IFileSystem fs, CancellationToken ct)
    {
        try
        {
            if (!fs.DirectoryExists(path)) return null;
            var installed = await new InstallManifestWriter(fs).ReadAsync(path, ct);
            if (installed is null || !string.Equals(installed.AppId, config.AppId, StringComparison.Ordinal)) return null;
            return new ExistingInstallation(path,
                installed.InstalledPerUser ? InstallationScope.PerUser : InstallationScope.SystemWide, installed.Version, source);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The newest installed version of this app: the one in <c>--path</c> when given, else the
    /// newest of <see cref="FindInstallationsAsync"/>. The newer-version offer skips a version
    /// that is installed already.
    /// </summary>
    public static async Task<Version?> FindAsync(
        FrozenConfig config, DispatchResult dispatch, IPlatformServices platform, IFileSystem fs, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(dispatch.InstallPath))
            return (await ReadAtAsync(config, dispatch.InstallPath!, "--path", fs, ct))?.Version;

        Version? newest = null;
        foreach (var installation in await FindInstallationsAsync(config, platform, fs, ct))
            if (newest is null || AppVersions.Compare(installation.Version, newest) > 0) newest = installation.Version;
        return newest;
    }
}

/// <summary>
/// A re-install targets the existing installation instead of creating a second copy that
/// takes over the Installed Apps entry, the shortcuts and (machine vs user) a second Run entry.
/// </summary>
internal static class ExistingInstallTargeting
{
    /// <summary>
    /// Applies the targeting rules before the scope is resolved. Returns the dispatch to go on
    /// with, or the usage error (exit 40) to stop with.
    /// </summary>
    public static async Task<(DispatchResult Dispatch, string? Error)> ApplyAsync(
        FrozenConfig config, DispatchResult dispatch, IPlatformServices platform, IFileSystem fs, CancellationToken ct)
    {
        var requested = ScopeArgument(dispatch.ScopeArgument);

        // --path given: the scope comes from that folder's installation, when there is one.
        if (!string.IsNullOrEmpty(dispatch.InstallPath))
        {
            var atPath = await InstalledVersionProbe.ReadAtAsync(config, dispatch.InstallPath!, "--path", fs, ct);
            if (atPath is null) return (dispatch, null);
            if (requested is { } r && r != atPath.Scope)
                return (dispatch, Contradiction(config, atPath));
            return (dispatch with { Existing = atPath }, null);
        }

        var found = await InstalledVersionProbe.FindInstallationsAsync(config, platform, fs, ct);
        switch (found.Count)
        {
            case 0:
                return (dispatch, null);
            case 1:
                if (requested is { } r && r != found[0].Scope)
                    return (dispatch, Contradiction(config, found[0]));
                return (dispatch with { InstallPath = found[0].Path, Existing = found[0] }, null);
            default:
                // Installed for the user and for the machine: --scope picks one; silent without
                // it cannot guess; interactive asks on the scope page.
                if (requested is { } scope && found.FirstOrDefault(f => f.Scope == scope) is { } chosen)
                    return (dispatch with { InstallPath = chosen.Path, Existing = chosen }, null);
                if (dispatch.IsSilent)
                    return (dispatch, $"{config.AppName} is installed twice ({found[0].Path}, {found[1].Path}); " +
                                      "choose one with --scope or --path");
                return (dispatch with { Installations = found }, null);
        }
    }

    /// <summary>After the scope page: the installation of the chosen scope, when there are two.</summary>
    public static DispatchResult SelectForScope(DispatchResult dispatch)
    {
        if (dispatch.Installations is not { Count: > 0 } all || dispatch.Scope is not { } scope || dispatch.Existing is not null)
            return dispatch;
        return all.FirstOrDefault(i => i.Scope == scope) is { } chosen
            ? dispatch with { InstallPath = chosen.Path, Existing = chosen }
            : dispatch;
    }

    /// <summary>The note the scope page shows when the app is installed for the user and for the machine.</summary>
    public static string? TwoInstallationsNotice(FrozenConfig config, DispatchResult dispatch)
    {
        if (dispatch.Installations is not { Count: > 1 } all) return null;
        var list = string.Join("; ", all.Select(i =>
            $"{i.Version} {(i.Scope == InstallationScope.PerUser ? "for you" : "for all users")} in {i.Path}"));
        return $"{config.AppName} is installed more than once ({list}). Choose the one to upgrade.";
    }

    private static string Contradiction(FrozenConfig config, ExistingInstallation existing) =>
        existing.Scope == InstallationScope.SystemWide
            ? $"{config.AppName} in {existing.Path} is installed for all users; use --scope machine or omit --scope"
            : $"{config.AppName} in {existing.Path} is installed for the current user; use --scope user or omit --scope";

    private static InstallationScope? ScopeArgument(string? value) => value?.ToLowerInvariant() switch
    {
        "user" => InstallationScope.PerUser,
        "machine" => InstallationScope.SystemWide,
        _ => null,
    };
}
