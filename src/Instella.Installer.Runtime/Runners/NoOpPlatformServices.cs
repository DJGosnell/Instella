using System.Diagnostics;
using Instella.Core.Platform;

namespace Instella.Installer.Runtime.Runners;

/// <summary>
/// Preview-only <see cref="IPlatformServices"/> shim that records nothing and
/// succeeds for every call. Used by <see cref="PreviewModeRunner"/> to
/// populate an <see cref="Installation.InstallContext"/> for the simulated
/// step pipeline — which never actually invokes step bodies, so no service
/// method is expected to run.
/// </summary>
/// <remarks>
/// Lives in Runtime rather than reusing <c>FakePlatformServices</c> from
/// <c>Instella.Installer.Testing</c> because Testing project references
/// Runtime, not the other way around. Keeping the shim local preserves the
/// dependency direction.
/// </remarks>
internal sealed class NoOpPlatformServices : IPlatformServices
{
    private readonly TargetPlatform _platform;

    public NoOpPlatformServices(TargetPlatform platform) => _platform = platform;

    public TargetPlatform Platform => _platform;

    public string GetDefaultInstallPath(string appName, bool perUser) => appName;

    public Task<PlatformResult> CreateShortcutAsync(ShortcutInfo info, CancellationToken ct) => Task.FromResult(PlatformResult.Ok);
    public Task<PlatformResult> RemoveShortcutAsync(ShortcutInfo info, CancellationToken ct) => Task.FromResult(PlatformResult.Ok);
    public Task<PlatformResult> RegisterFileAssociationAsync(FileAssociationInfo info, CancellationToken ct) => Task.FromResult(PlatformResult.Ok);
    public Task<PlatformResult> UnregisterFileAssociationAsync(string extension, string appId, CancellationToken ct) => Task.FromResult(PlatformResult.Ok);
    public Task<PlatformResult> AddToPathAsync(string directory, bool perUser, CancellationToken ct) => Task.FromResult(PlatformResult.Ok);
    public Task<PlatformResult> RemoveFromPathAsync(string directory, bool perUser, CancellationToken ct) => Task.FromResult(PlatformResult.Ok);
    public Task<PlatformResult> ConfigureAutoStartAsync(AutoStartInfo info, CancellationToken ct) => Task.FromResult(PlatformResult.Ok);
    public Task<PlatformResult> RemoveAutoStartAsync(string appId, CancellationToken ct) => Task.FromResult(PlatformResult.Ok);

    public IReadOnlyList<Process> GetRunningProcesses(string processName, string? installPath) => [];

    public Task<PlatformResult> TerminateProcessAsync(Process process, TimeSpan timeout, CancellationToken ct) => Task.FromResult(PlatformResult.Ok);
    public Task<PlatformResult> UpdateShortcutAsync(string oldTarget, string newTarget, string shortcutPath, CancellationToken ct) => Task.FromResult(PlatformResult.Ok);
    public Task<PlatformResult> RegisterUninstallEntryAsync(UninstallEntryInfo info, CancellationToken ct) => Task.FromResult(PlatformResult.Ok);
    public Task<PlatformResult> UnregisterUninstallEntryAsync(string appId, bool perUser, CancellationToken ct) => Task.FromResult(PlatformResult.Ok);
}
