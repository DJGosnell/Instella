using Instella.Core.Platform.Linux;
using Instella.Core.Platform.MacOS;
using Instella.Core.Platform.Windows;

namespace Instella.Core.Platform;

/// <summary>
/// Factory for creating platform-specific services.
/// </summary>
#pragma warning disable CA1416 // Validate platform compatibility - we use runtime detection
internal static class PlatformServicesFactory
{
    private static IPlatformServices? _instance;

    /// <summary>
    /// Creates a new instance of the appropriate platform services for the current OS.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">If the current OS is not supported.</exception>
    public static IPlatformServices Create()
    {
        return PlatformDetector.Current switch
        {
            TargetPlatform.Windows => new WindowsPlatformServices(),
            TargetPlatform.Linux => new LinuxPlatformServices(),
            TargetPlatform.MacOS => new MacOSPlatformServices(),
            _ => throw new PlatformNotSupportedException($"Platform {PlatformDetector.Current} is not supported.")
        };
    }

    /// <summary>
    /// Gets a singleton instance of the platform services for the current OS.
    /// </summary>
    public static IPlatformServices Instance => _instance ??= Create();
}
#pragma warning restore CA1416
