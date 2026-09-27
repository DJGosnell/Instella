using System.Runtime.InteropServices;

namespace Instella.Core.Platform;

/// <summary>
/// CPU architecture.
/// </summary>
public enum Architecture : byte
{
    /// <summary>64-bit x86 (AMD64/Intel 64).</summary>
    X64 = 1,

    /// <summary>32-bit x86.</summary>
    X86 = 2,

    /// <summary>64-bit ARM.</summary>
    ARM64 = 3,

    /// <summary>32-bit ARM.</summary>
    ARM32 = 4
}

/// <summary>
/// Extension methods for Architecture enum.
/// </summary>
public static class ArchitectureExtensions
{
    /// <summary>
    /// Gets the current process architecture.
    /// </summary>
    public static Architecture Current => RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => Architecture.X64,
        System.Runtime.InteropServices.Architecture.X86 => Architecture.X86,
        System.Runtime.InteropServices.Architecture.Arm64 => Architecture.ARM64,
        System.Runtime.InteropServices.Architecture.Arm => Architecture.ARM32,
        _ => throw new PlatformNotSupportedException($"Unsupported architecture: {RuntimeInformation.ProcessArchitecture}")
    };

    /// <summary>
    /// Parses an architecture name to an Architecture value.
    /// </summary>
    public static Architecture Parse(string archName)
    {
        return archName.ToLowerInvariant() switch
        {
            "x64" or "amd64" => Architecture.X64,
            "x86" or "i386" or "i686" => Architecture.X86,
            "arm64" or "aarch64" => Architecture.ARM64,
            "arm" or "arm32" or "armv7" => Architecture.ARM32,
            _ => throw new ArgumentException($"Unknown architecture: {archName}", nameof(archName))
        };
    }

    /// <summary>
    /// Gets the canonical name for an architecture.
    /// </summary>
    public static string GetName(this Architecture arch) => arch switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.ARM64 => "arm64",
        Architecture.ARM32 => "arm32",
        _ => throw new ArgumentOutOfRangeException(nameof(arch))
    };
}
