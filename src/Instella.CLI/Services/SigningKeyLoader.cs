using System.Security.Cryptography;
using Instella.Core.Trust;

namespace Instella.CLI.Services;

/// <summary>
/// Resolves the publisher signing key for <c>upload</c>: <c>--signing-key</c>, else the
/// <c>INSTELLA_SIGNING_KEY</c> environment variable. Either holds PEM text or a path to a
/// PEM file; the password comes from <c>INSTELLA_SIGNING_KEY_PASSWORD</c>.
/// </summary>
internal static class SigningKeyLoader
{
    public const string KeyEnvironmentVariable = "INSTELLA_SIGNING_KEY";
    public const string PasswordEnvironmentVariable = "INSTELLA_SIGNING_KEY_PASSWORD";

    /// <summary>Returns the key, or null when none was configured.</summary>
    /// <exception cref="ArgumentException">A key was configured but cannot be read.</exception>
    public static ECDsa? Resolve(string? explicitValue)
    {
        var value = !string.IsNullOrEmpty(explicitValue)
            ? explicitValue
            : Environment.GetEnvironmentVariable(KeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!string.IsNullOrEmpty(explicitValue) && explicitValue.Contains("-----BEGIN", StringComparison.Ordinal))
            Console.Error.WriteLine($"Warning: --signing-key holds the key itself, which other users can see in the process list; " +
                $"pass a file path or set {KeyEnvironmentVariable} instead.");
        var pem = value.Contains("-----BEGIN", StringComparison.Ordinal)
            ? value
            : File.Exists(value)
                ? File.ReadAllText(value)
                : throw new ArgumentException($"Signing key '{value}' is neither PEM text nor an existing file.");
        return Load(pem, Environment.GetEnvironmentVariable(PasswordEnvironmentVariable));
    }

    /// <summary>Imports PEM text.</summary>
    public static ECDsa Load(string pem, string? password) => ReleaseKeys.ImportPrivateKeyPem(pem, password);
}
