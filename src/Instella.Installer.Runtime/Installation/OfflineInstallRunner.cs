using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Installation.BuiltIn;
using Instella.Installer.Runtime.Logging;

namespace Instella.Installer.Runtime.Installation;

/// <summary>
/// Thin composition layer: builds the default ordered list of built-in steps
/// for an offline (embedded-archive) first-install, wires up
/// <see cref="InstallContext"/>, drives <see cref="StepExecutor"/>, and maps
/// the <see cref="ExecutionResult"/> back to the
/// <see cref="InstallationResult"/> shape. The mode runners use
/// <see cref="BuildDefaultSteps"/> for the built-in step list.
/// </summary>
internal sealed class OfflineInstallRunner
{
    private readonly InstellaManifest _manifest;
    private readonly IPlatformServices _platform;
    private readonly IFileSystem _fileSystem;
    private readonly IInstellaLogger _log;

    public OfflineInstallRunner(InstellaManifest manifest, IPlatformServices platform, IFileSystem fileSystem)
        : this(manifest, platform, fileSystem, log: null) { }

    public OfflineInstallRunner(InstellaManifest manifest, IPlatformServices platform, IFileSystem fileSystem, IInstellaLogger? log)
    {
        _manifest = manifest;
        _platform = platform;
        _fileSystem = fileSystem;
        _log = log ?? new LogsmithLoggerAdapter();
    }

    public async Task<InstallationResult> RunAsync(
        Stream payload,
        InstallOptions options,
        IProgress<OverallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var scope = ResolveScope(options);
        var context = new InstallContext
        {
            AppName = _manifest.AppName,
            AppId = _manifest.AppId,
            AppVersion = _manifest.Version,
            InstallPath = options.InstallPath,
            Mode = InstallerMode.FirstInstall,
            Scope = scope,
            Manifest = _manifest,
            Options = options,
            Platform = _platform,
            FileSystem = _fileSystem,
            Log = _log,
            PayloadArchive = payload,
        };

        var steps = BuildDefaultSteps();
        var executor = new StepExecutor(steps);

        var result = await executor.ExecuteAsync(context, progress, cancellationToken);

        if (!result.Success)
        {
            if (cancellationToken.IsCancellationRequested)
                return InstallationResult.Cancelled();
            return InstallationResult.Failed(result.Error ?? "installation failed");
        }

        if (result.Warnings.Count > 0)
        {
            var preview = string.Join("; ", result.Warnings);
            if (preview.Length > 240) preview = preview[..237] + "...";
            return InstallationResult.SucceededWithWarning(context.InstallPath, preview);
        }

        return InstallationResult.Succeeded(context.InstallPath);
    }

    internal static IReadOnlyList<IInstallStepExecution> BuildDefaultSteps() => new IInstallStepExecution[]
    {
        new PrerequisitesStep(),
        new ExtractPayloadStep(),
        new CreateShortcutsStep(),
        new RegisterFileAssociationsStep(),
        new AddToPathStep(),
        new ConfigureAutoStartStep(),
        new RemoveDroppedIntegrationsStep(),
        new StageUninstallerStubStep(),
        new RegisterUninstallEntryStep(),
        new WriteManifestStep(),
        new CommitTransactionStep(),
        new AppUpgradeStep(),
    };

    /// <summary>
    /// Resolves install scope from <see cref="InstallOptions.Elevation"/>.
    /// Explicit PerUser / SystemWide wins; UserChoice infers from the chosen path.
    /// </summary>
    internal static InstallationScope ResolveScope(InstallOptions options)
    {
        return options.Elevation switch
        {
            ElevationMode.PerUser => InstallationScope.PerUser,
            ElevationMode.SystemWide => InstallationScope.SystemWide,
            _ => IsPerUserInstallPath(options.InstallPath) ? InstallationScope.PerUser : InstallationScope.SystemWide,
        };
    }

    private static bool IsPerUserInstallPath(string installPath)
    {
        if (string.IsNullOrEmpty(installPath)) return true;

        var userProfile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile)) return false;

        var normalizedInstall = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath));
        var normalizedProfile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userProfile));
        return normalizedInstall.StartsWith(normalizedProfile, System.StringComparison.OrdinalIgnoreCase);
    }
}
