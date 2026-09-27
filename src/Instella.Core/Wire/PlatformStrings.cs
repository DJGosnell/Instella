using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Instella.Core.Platform;
using Architecture = Instella.Core.Platform.Architecture;

namespace Instella.Core.Wire;

/// <summary>
/// The single mapping between platform enums and the names exchanged on the wire.
/// Clients and server both send and parse through this type, so the two name sets
/// cannot drift apart.
/// </summary>
public static class PlatformStrings
{
    /// <summary>Canonical wire name for an operating system (<c>windows</c>, <c>linux</c>, <c>macos</c>).</summary>
    public static string Os(TargetPlatform platform) => platform switch
    {
        TargetPlatform.Windows => "windows",
        TargetPlatform.Linux => "linux",
        TargetPlatform.MacOS => "macos",
        _ => throw new ArgumentOutOfRangeException(nameof(platform)),
    };

    /// <summary>Canonical wire name for an architecture (<c>x64</c>, <c>x86</c>, <c>arm64</c>, <c>arm32</c>).</summary>
    public static string Arch(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.ARM64 => "arm64",
        Architecture.ARM32 => "arm32",
        _ => throw new ArgumentOutOfRangeException(nameof(architecture)),
    };

    /// <summary>Parses an OS name case-insensitively. Accepts <c>windows|win</c>, <c>linux</c>, <c>macos|osx</c>.</summary>
    public static bool TryParseOs([NotNullWhen(true)] string? value, out TargetPlatform platform)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "windows" or "win": platform = TargetPlatform.Windows; return true;
            case "linux": platform = TargetPlatform.Linux; return true;
            case "macos" or "osx": platform = TargetPlatform.MacOS; return true;
            default: platform = default; return false;
        }
    }

    /// <summary>
    /// Parses an architecture name case-insensitively. Accepts <c>x64|amd64</c>, <c>x86|i386|i686</c>,
    /// <c>arm64|aarch64</c>, <c>arm32|arm|armv7</c>.
    /// </summary>
    public static bool TryParseArch([NotNullWhen(true)] string? value, out Architecture architecture)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "x64" or "amd64": architecture = Architecture.X64; return true;
            case "x86" or "i386" or "i686": architecture = Architecture.X86; return true;
            case "arm64" or "aarch64": architecture = Architecture.ARM64; return true;
            case "arm32" or "arm" or "armv7": architecture = Architecture.ARM32; return true;
            default: architecture = default; return false;
        }
    }

    /// <summary>All canonical OS names, for error messages.</summary>
    public static IReadOnlyList<string> OsNames { get; } = ["windows", "linux", "macos"];

    /// <summary>All canonical architecture names, for error messages.</summary>
    public static IReadOnlyList<string> ArchNames { get; } = ["x64", "x86", "arm64", "arm32"];

    /// <summary>
    /// The operating system's architecture. Note that an installed build records the
    /// architecture it was <em>installed as</em>; prefer that over this value for update
    /// requests, because an x64 build under emulation on ARM64 reports ARM64 here.
    /// </summary>
    public static Architecture Current() => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => Architecture.X64,
        System.Runtime.InteropServices.Architecture.X86 => Architecture.X86,
        System.Runtime.InteropServices.Architecture.Arm64 => Architecture.ARM64,
        System.Runtime.InteropServices.Architecture.Arm => Architecture.ARM32,
        var other => throw new PlatformNotSupportedException($"Unsupported architecture {other}"),
    };
}
