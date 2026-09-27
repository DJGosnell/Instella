namespace Instella.Core.Platform;

/// <summary>
/// Windows registry hive selector for tracked-rollback registry operations
/// and the fluent registry sub-builder. Linux/macOS implementations
/// of <see cref="IPlatformServices"/> ignore the value.
/// </summary>
/// <remarks>
/// <see cref="AutoFromScope"/> resolves at write time: per-user installs map
/// to <see cref="CurrentUser"/>, system-wide installs to <see cref="LocalMachine"/>.
/// </remarks>
public enum RegistryHive
{
    /// <summary><see cref="CurrentUser"/> for per-user installs, <see cref="LocalMachine"/> for machine-wide ones.</summary>
    AutoFromScope = 0,
    /// <summary><c>HKEY_CURRENT_USER</c>.</summary>
    CurrentUser,
    /// <summary><c>HKEY_LOCAL_MACHINE</c>.</summary>
    LocalMachine,
}
