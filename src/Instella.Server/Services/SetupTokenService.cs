using System.Security.Cryptography;
using System.Text;

namespace Instella.Server.Services;

/// <summary>
/// First-run setup token: while no admin exists, <c>/setup</c> requires a random
/// token written to <c>{configDir}/setup-token</c>, so whoever first reaches a freshly exposed
/// server cannot make themselves admin without access to its files.
/// </summary>
public sealed class SetupTokenService(string configDir, ILogger<SetupTokenService> logger)
{
    public string TokenPath => Path.Combine(configDir, "setup-token");

    /// <summary>Writes a new token unless one exists (called at startup when no admin exists).</summary>
    public void EnsureToken()
    {
        if (File.Exists(TokenPath))
        {
            logger.LogWarning("Setup is not complete. Setup token: {Path}", TokenPath);
            return;
        }

        Directory.CreateDirectory(configDir);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(TokenPath, token);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        logger.LogWarning("Setup is not complete. Setup token written to {Path}", TokenPath);
    }

    /// <summary>Constant-time comparison with the token on disk.</summary>
    public bool IsValid(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(TokenPath)) return false;
        var expected = File.ReadAllText(TokenPath).Trim();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(candidate.Trim()));
    }

    /// <summary>Deletes the token once the first admin exists.</summary>
    public void Consume()
    {
        try { File.Delete(TokenPath); }
        catch (IOException ex) { logger.LogWarning(ex, "Could not delete the setup token {Path}", TokenPath); }
    }
}
