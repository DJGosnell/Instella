using System.Runtime.InteropServices;

namespace Instella.Core.Platform;

/// <summary>
/// Detects the current runtime platform.
/// </summary>
internal static class PlatformDetector
{
    private static readonly Lazy<TargetPlatform> _current = new(DetectPlatform);

    /// <summary>
    /// Gets the current runtime platform.
    /// </summary>
    public static TargetPlatform Current => _current.Value;

    /// <summary>
    /// Gets whether the current platform is Windows.
    /// </summary>
    public static bool IsWindows => Current == TargetPlatform.Windows;

    /// <summary>
    /// Gets whether the current platform is Linux.
    /// </summary>
    public static bool IsLinux => Current == TargetPlatform.Linux;

    /// <summary>
    /// Gets whether the current platform is macOS.
    /// </summary>
    public static bool IsMacOS => Current == TargetPlatform.MacOS;

    private static TargetPlatform DetectPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return TargetPlatform.Windows;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return TargetPlatform.Linux;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return TargetPlatform.MacOS;

        throw new PlatformNotSupportedException("Current operating system is not supported.");
    }

    /// <summary>
    /// Parses a platform name string to a TargetPlatform value.
    /// </summary>
    /// <param name="platformName">Platform name (windows, linux, macos, osx, win).</param>
    /// <returns>The parsed platform.</returns>
    /// <exception cref="ArgumentException">If the platform name is not recognized.</exception>
    public static TargetPlatform Parse(string platformName)
    {
        return platformName.ToLowerInvariant() switch
        {
            "windows" or "win" => TargetPlatform.Windows,
            "linux" => TargetPlatform.Linux,
            "macos" or "osx" => TargetPlatform.MacOS,
            _ => throw new ArgumentException($"Unknown platform: {platformName}", nameof(platformName))
        };
    }

    /// <summary>
    /// Tries to parse a platform name string to a TargetPlatform value.
    /// </summary>
    /// <param name="platformName">Platform name.</param>
    /// <param name="platform">The parsed platform if successful.</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParse(string platformName, out TargetPlatform platform)
    {
        platform = platformName.ToLowerInvariant() switch
        {
            "windows" or "win" => TargetPlatform.Windows,
            "linux" => TargetPlatform.Linux,
            "macos" or "osx" => TargetPlatform.MacOS,
            _ => default
        };
        return platform != default || platformName.ToLowerInvariant() is "windows" or "win";
    }

    /// <summary>
    /// Gets the canonical name for a platform.
    /// </summary>
    public static string GetName(TargetPlatform platform) => platform switch
    {
        TargetPlatform.Windows => "windows",
        TargetPlatform.Linux => "linux",
        TargetPlatform.MacOS => "macos",
        _ => throw new ArgumentOutOfRangeException(nameof(platform))
    };
}
