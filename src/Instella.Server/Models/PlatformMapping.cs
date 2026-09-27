using Instella.Core.Wire;
using CoreArch = Instella.Core.Platform.Architecture;
using CorePlatform = Instella.Core.Platform.TargetPlatform;

namespace Instella.Server.Models;

/// <summary>
/// The one place that maps wire names (via <see cref="PlatformStrings"/>) to the server's
/// persisted enums. The server enums are 0-based and Core's are 1-based; only names ever
/// cross the wire, so the numeric difference is harmless, but every parse must go
/// through here so the name sets cannot drift.
/// </summary>
public static class PlatformMapping
{
    /// <summary>Parses a wire OS name.</summary>
    public static bool TryParseOs(string? value, out TargetOS os)
    {
        os = default;
        if (!PlatformStrings.TryParseOs(value, out var p)) return false;
        os = ToServer(p);
        return true;
    }

    /// <summary>Parses a wire architecture name.</summary>
    public static bool TryParseArch(string? value, out Architecture arch)
    {
        arch = default;
        if (!PlatformStrings.TryParseArch(value, out var a)) return false;
        arch = ToServer(a);
        return true;
    }

    /// <summary>Canonical wire name of a server OS value.</summary>
    public static string ToWire(TargetOS os) => PlatformStrings.Os(ToCore(os));

    /// <summary>Canonical wire name of a server architecture value.</summary>
    public static string ToWire(Architecture arch) => PlatformStrings.Arch(ToCore(arch));

    /// <summary>Error text listing valid OS names.</summary>
    public static string InvalidOsMessage => $"Invalid OS. Use: {string.Join(", ", PlatformStrings.OsNames)}";

    /// <summary>Error text listing valid architecture names.</summary>
    public static string InvalidArchMessage => $"Invalid architecture. Use: {string.Join(", ", PlatformStrings.ArchNames)}";

    private static TargetOS ToServer(CorePlatform p) => p switch
    {
        CorePlatform.Windows => TargetOS.Windows,
        CorePlatform.Linux => TargetOS.Linux,
        CorePlatform.MacOS => TargetOS.MacOS,
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    private static Architecture ToServer(CoreArch a) => a switch
    {
        CoreArch.X64 => Architecture.X64,
        CoreArch.X86 => Architecture.X86,
        CoreArch.ARM64 => Architecture.ARM64,
        CoreArch.ARM32 => Architecture.ARM32,
        _ => throw new ArgumentOutOfRangeException(nameof(a)),
    };

    private static CorePlatform ToCore(TargetOS os) => os switch
    {
        TargetOS.Windows => CorePlatform.Windows,
        TargetOS.Linux => CorePlatform.Linux,
        TargetOS.MacOS => CorePlatform.MacOS,
        _ => throw new ArgumentOutOfRangeException(nameof(os)),
    };

    private static CoreArch ToCore(Architecture a) => a switch
    {
        Architecture.X64 => CoreArch.X64,
        Architecture.X86 => CoreArch.X86,
        Architecture.ARM64 => CoreArch.ARM64,
        Architecture.ARM32 => CoreArch.ARM32,
        _ => throw new ArgumentOutOfRangeException(nameof(a)),
    };
}
