using Instella.Core.Platform.Windows;

namespace Instella.Core.Platform;

/// <summary>Creates the service manager for a platform.</summary>
internal static class ServiceManagerFactory
{
    /// <summary>The service manager for the current OS.</summary>
    public static IServiceManager Create() => Create(PlatformDetector.Current);

    /// <summary>
    /// The SCM-backed manager for <see cref="TargetPlatform.Windows"/> on Windows; otherwise one
    /// that fails every call naming <paramref name="platform"/>.
    /// </summary>
    public static IServiceManager Create(TargetPlatform platform) =>
        platform == TargetPlatform.Windows && OperatingSystem.IsWindows()
            ? new WindowsServiceManager()
            : new UnsupportedServiceManager(platform);
}
