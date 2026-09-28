using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Installation;

namespace Instella.Installer.Runtime.Runners;

/// <summary>
/// Builds the <see cref="InstallContext"/> shared by the headless
/// <see cref="InstallModeRunner"/> and the interactive
/// <see cref="InteractiveInstallRunner"/> so the two paths cannot drift on
/// scope resolution, manifest-flag projection, or logging config. Extracted
/// during REMEDIATE after the interactive path was found to hardcode
/// <see cref="InstallationScope.PerUser"/> (ignoring <c>WithElevation</c>)
/// where the headless path correctly derived scope from the install options.
/// </summary>
internal static class InstallContextFactory
{
    /// <summary>
    /// Assemble a fully-populated <see cref="InstallContext"/> from the frozen
    /// builder config and the resolved install options. <see cref="InstallContext.Scope"/>
    /// is derived from <see cref="InstallOptions.Elevation"/> / the install path
    /// via <see cref="ResolveScope"/>. The caller may pass an already-open
    /// <paramref name="payload"/> or set <see cref="InstallContext.PayloadArchive"/>
    /// later (the interactive path opens the payload after the context exists).
    /// </summary>
    internal static InstallContext Create(
        FrozenConfig config,
        InstallerMode mode,
        string installPath,
        InstallOptions options,
        IPlatformServices platform,
        IFileSystem fileSystem,
        IInstellaLogger log,
        Stream? payload = null,
        InstalledManifest? existing = null,
        CliArgs? cli = null,
        IReadOnlyDictionary<string, UI.Widgets.PageState>? pages = null,
        bool allowElevationPrompt = false)
    {
        return new InstallContext
        {
            AppName = config.AppName,
            AppId = config.AppId,
            AppVersion = config.AppVersion,
            InstallPath = installPath,
            Mode = mode,
            Scope = ResolveScope(options),
            Manifest = RuntimeManifest(config),
            Options = options,
            Platform = platform,
            FileSystem = fileSystem,
            Log = log,
            PayloadArchive = payload,
            ExistingInstallation = existing,
            Cli = cli ?? CliArgs.Empty,
            AllowElevationPrompt = allowElevationPrompt,
            AppManagedRunValues = config.AppManagedRunValuesOrEmpty,
            Pages = pages ?? new Dictionary<string, UI.Widgets.PageState>(StringComparer.Ordinal),
            ManifestCliFlags = config.DeclaredCliFlags.Count > 0
                ? config.DeclaredCliFlags
                    .Select(f => new ManifestCliFlag(f.Name, f.ValueType.Name, f.MapsTo))
                    .ToList()
                : null,
            ManifestLogging = new ManifestLoggingConfig(
                config.Logging.Level ?? InstellaLogLevel.Info,
                config.Logging.FilePath,
                RetainCount: 10),
        };
    }

    /// <summary>
    /// The build manifest as the running installer sees it. The builder's icon source is a
    /// build-machine path (for example <c>../App/Assets/icon.ico</c>); the build embeds that
    /// icon in the payload as <see cref="InstellaOwnedPaths.AppIcon"/>, so at install time the
    /// app icon, and any file association that reuses the same source, point there instead.
    /// </summary>
    internal static InstellaManifest RuntimeManifest(FrozenConfig config)
    {
        var manifest = config.ToManifest();
        if (config.Icon is null) return manifest;
        var appIconSource = manifest.IconPath;
        return manifest with
        {
            IconPath = InstellaOwnedPaths.AppIcon,
            FileAssociations = manifest.FileAssociations?
                .Select(a => appIconSource is not null && string.Equals(a.IconPath, appIconSource, StringComparison.Ordinal)
                    ? a with { IconPath = InstellaOwnedPaths.AppIcon }
                    : a)
                .ToList(),
        };
    }

    /// <summary>
    /// Produce a copy of <paramref name="original"/> rooted at
    /// <paramref name="newPath"/> (both <see cref="InstallContext.InstallPath"/>
    /// and the nested <see cref="InstallOptions.InstallPath"/>). Used to apply an
    /// <c>WithInstallPath</c> resolver override after the context exists.
    /// </summary>
    internal static InstallContext WithInstallPath(InstallContext original, string newPath)
    {
        return new InstallContext
        {
            AppName = original.AppName,
            AppId = original.AppId,
            AppVersion = original.AppVersion,
            InstallPath = newPath,
            Mode = original.Mode,
            Scope = original.Scope,
            Manifest = original.Manifest,
            Options = original.Options with { InstallPath = newPath },
            Platform = original.Platform,
            FileSystem = original.FileSystem,
            Log = original.Log,
            PayloadArchive = original.PayloadArchive,
            ExistingInstallation = original.ExistingInstallation,
            Cli = original.Cli,
            Pages = original.Pages,
            ManifestCliFlags = original.ManifestCliFlags,
            ManifestLogging = original.ManifestLogging,
            AppManagedRunValues = original.AppManagedRunValues,
            Migrations = original.Migrations,
        };
    }

    /// <summary>
    /// Derive the install scope from the elevation choice, falling back to a
    /// per-user/system-wide guess from the install path when elevation is left
    /// at the default. Mirrors the original headless-path logic so both runners
    /// resolve scope identically.
    /// </summary>
    internal static InstallationScope ResolveScope(InstallOptions options)
    {
        // ElevationMode is fully qualified: its namespace (Instella.Core.Manifest)
        // also defines a ManifestCliFlag that would collide with the
        // Instella.Installer.Runtime.Core one used above if imported wholesale.
        return options.Elevation switch
        {
            Instella.Core.Manifest.ElevationMode.PerUser => InstallationScope.PerUser,
            Instella.Core.Manifest.ElevationMode.SystemWide => InstallationScope.SystemWide,
            _ => IsPerUserInstallPath(options.InstallPath) ? InstallationScope.PerUser : InstallationScope.SystemWide,
        };
    }

    /// <summary>
    /// True when <paramref name="installPath"/> sits under the current user's
    /// profile directory — used as the scope heuristic when elevation is
    /// unspecified.
    /// </summary>
    internal static bool IsPerUserInstallPath(string installPath)
    {
        if (string.IsNullOrEmpty(installPath)) return true;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile)) return false;

        var normalizedInstall = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath));
        var normalizedProfile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userProfile));
        return normalizedInstall.StartsWith(normalizedProfile, StringComparison.OrdinalIgnoreCase);
    }
}
