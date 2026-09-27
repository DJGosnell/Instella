namespace Instella.Server;

/// <summary>Permissions for the config directory (database, Data Protection keys, setup token).</summary>
internal static class ConfigDirectory
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Makes <paramref name="path"/> owner-only (0700) off Windows, when it is not already. A
    /// directory the server does not own (a bind mount from the host, or one owned by another
    /// uid) cannot be changed; that is not fatal, and the returned warning says to set the
    /// permissions on the host instead. On Windows the directory keeps its inherited ACL; the
    /// Data Protection keys are DPAPI-protected there.
    /// </summary>
    /// <returns>Null when the directory is owner-only, otherwise a warning.</returns>
    public static string? RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return null;
        try
        {
            if ((File.GetUnixFileMode(path) & ~OwnerOnly) != 0)
                File.SetUnixFileMode(path, OwnerOnly);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return $"warning: could not restrict '{path}' to its owner (0700): {ex.Message} " +
                "It holds the database and the Data Protection keys; set its permissions on the host (chmod 700).";
        }
    }
}
