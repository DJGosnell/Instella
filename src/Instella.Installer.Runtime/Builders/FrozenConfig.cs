using System;
using System.Collections.Generic;
using Instella.Core.Logging;
using Instella.Core.Manifest;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Installation.Builders;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Immutable snapshot of an <see cref="InstallerBuilder"/> at the moment
/// <see cref="InstallerBuilder.Build"/> was called. Every mode runner and
/// the <see cref="InstellaInstallerImpl"/> reads from this record — the
/// mutable builder is not touched after <c>Build()</c>.
/// </summary>
/// <remarks>
/// The built-in steps read an <see cref="InstellaManifest"/>, which the install path builds on
/// demand from this record (see <see cref="FrozenConfigExtensions.ToManifest"/>).
/// </remarks>
internal sealed record FrozenConfig(
    string AppName,
    string AppId,
    Version AppVersion,
    string ServerUrl,
    string Channel,
    string? Publisher,
    string? HomepageUrl,
    string? LicenseUrl,
    string? Description,
    ImageSource? Icon,
    Func<InstallContext, string>? InstallPathResolver,
    ElevationMode Elevation,
    string? ExecutableName,
    ShortcutConfig? Shortcuts,
    IReadOnlyList<FileAssociation> FileAssociations,
    bool AutoStart,
    bool PathRegistration,
    IReadOnlyList<Prerequisite> Prerequisites,
    IReadOnlyList<StepSpec> UserSteps,
    IReadOnlyList<PageSpec> Pages,
    IReadOnlyList<RegistryWriteSpec> RegistryWrites,
    LoggingBuilder Logging,
    IReadOnlyList<CliFlagSpec> DeclaredCliFlags,
    bool PreviewEnabled,
    PayloadFilter? PayloadFilter,
    IReadOnlyList<Instella.Core.Trust.PublisherKey> PublisherKeys,
    bool AllowUnsignedUpdates,
    bool AllowInsecureServer,
    bool? OfferLaunchAfterInstall = null,
    bool OfferNewerVersion = false,
    bool AllowVersionSelection = false,
    ImageSource? BrandImage = null,
    string? DownloadToken = null,
    IReadOnlyList<Migrations.InstallMigration>? Migrations = null,
    IReadOnlyList<string>? AppManagedRunValues = null)
{
    /// <summary>The registered migrations, sorted (timing, order, id); empty when none.</summary>
    public IReadOnlyList<Migrations.InstallMigration> MigrationsOrEmpty => Migrations ?? [];

    /// <summary>The <c>WithAppManagedAutoStart</c> value names; empty when none.</summary>
    public IReadOnlyList<string> AppManagedRunValuesOrEmpty => AppManagedRunValues ?? [];
}

internal static class FrozenConfigExtensions
{
    /// <summary>
    /// Projects a <see cref="FrozenConfig"/> into the existing
    /// <see cref="InstellaManifest"/> shape that built-in steps consume. Kept internal so the manifest cannot be
    /// mutated by user code between Build() and RunAsync().
    /// </summary>
    public static InstellaManifest ToManifest(this FrozenConfig config) => new()
    {
        AppName = config.AppName,
        AppId = config.AppId,
        Version = config.AppVersion,
        ServerUrl = config.ServerUrl,
        ExecutableName = config.ExecutableName,
        Shortcuts = config.Shortcuts,
        PathRegistration = config.PathRegistration,
        AutoStart = config.AutoStart,
        FileAssociations = config.FileAssociations.Count > 0 ? config.FileAssociations : null,
        Prerequisites = config.Prerequisites.Count > 0 ? config.Prerequisites : null,
        Elevation = config.Elevation,
        // Only file-sourced icons project into the manifest's string
        // IconPath field. Resource- and bytes-sourced icons exist solely on
        // FrozenConfig; renderers will materialize them at install time.
        IconPath = config.Icon is FileImageSource file ? file.Path : null,
        Channel = config.Channel,
        Description = config.Description,
        Publisher = config.Publisher,
        HomepageUrl = config.HomepageUrl,
        LicenseUrl = config.LicenseUrl,
        PayloadFilter = config.PayloadFilter,
        PublisherKeys = config.PublisherKeys.Count > 0 ? config.PublisherKeys : null,
        AllowUnsignedUpdates = config.AllowUnsignedUpdates,
        AllowInsecureServer = config.AllowInsecureServer,
        DownloadToken = config.DownloadToken,
    };
}
