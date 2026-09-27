using System.CommandLine;
using System.Security.Cryptography;
using Instella.Core.Trust;

namespace Instella.CLI.Services;

/// <summary>
/// The signing options shared by commands that sign releases: <c>--signing-key</c> (a PEM key
/// in this process) or <c>--sign-command</c> + <c>--signing-public-key</c> (a key the command
/// uses without exposing it, such as a cloud KMS key), each with an environment fallback.
/// </summary>
internal sealed class SigningOptions
{
    public Option<string?> SigningKey { get; } = new("--signing-key")
    {
        Description = $"Publisher private key: PEM text or a PEM file path (default: {SigningKeyLoader.KeyEnvironmentVariable}; " +
                      $"password from {SigningKeyLoader.PasswordEnvironmentVariable})",
    };

    public Option<string?> SignCommand { get; } = new("--sign-command")
    {
        Description = "Sign with an external command instead of a local key (a KMS/HSM key that never leaves its service). " +
                      "Placeholders: {digest-hex} {digest-base64} {digest-base64url} {digest-file} {message-file}; it prints the " +
                      $"signature (base64/hex, raw or DER, or JSON with result/signature). Default: {CommandSigningKey.CommandEnvironmentVariable}",
    };

    public Option<string?> SigningPublicKey { get; } = new("--signing-public-key")
    {
        Description = "The public key (base64, as 'keys show' prints) that --sign-command signs with; every signature is checked " +
                      $"against it before upload. Default: {CommandSigningKey.PublicKeyEnvironmentVariable}",
    };

    public IEnumerable<Option> All => [SigningKey, SignCommand, SigningPublicKey];

    /// <summary>
    /// The configured key, or null when none is. Throws <see cref="ArgumentException"/> for a
    /// configuration that cannot work (both kinds, a command without its public key, a bad key).
    /// </summary>
    public IReleaseSigningKey? Resolve(ParseResult parse, out IDisposable? owned)
    {
        owned = null;
        var command = Value(parse.GetValue(SignCommand), CommandSigningKey.CommandEnvironmentVariable);
        var explicitKey = parse.GetValue(SigningKey);
        if (command is not null)
        {
            if (!string.IsNullOrEmpty(explicitKey))
                throw new ArgumentException("--signing-key and --sign-command cannot be combined; pass one of them.");
            var publicKey = Value(parse.GetValue(SigningPublicKey), CommandSigningKey.PublicKeyEnvironmentVariable)
                ?? throw new ArgumentException("--sign-command needs --signing-public-key (or " +
                                               $"{CommandSigningKey.PublicKeyEnvironmentVariable}): the public key the command signs with.");
            return new CommandSigningKey(command, KeyIds.FromPublicKey(publicKey));
        }

        var local = SigningKeyLoader.Resolve(explicitKey);
        owned = local;
        return local is null ? null : new LocalSigningKey(local);
    }

    private static string? Value(string? option, string environmentVariable)
    {
        var value = !string.IsNullOrWhiteSpace(option) ? option : Environment.GetEnvironmentVariable(environmentVariable);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
