using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.DataProtection;

namespace Instella.Server.Services;

/// <summary>
/// Protects secrets at rest: the S3 secret key and TOTP secrets are stored
/// encrypted with ASP.NET Core Data Protection (purpose <c>Instella.Secrets.v1</c>), whose keys
/// live in <c>{configDir}/keys</c>. Services protect on write and unprotect on read.
/// </summary>
public sealed class SecretProtector
{
    private readonly IDataProtector? _protector;

    public SecretProtector(IDataProtectionProvider dataProtection) =>
        _protector = dataProtection.CreateProtector("Instella.Secrets.v1");

    private SecretProtector() => _protector = null;

    /// <summary>No encryption, for unit tests that construct services by hand.</summary>
    public static SecretProtector None { get; } = new();

    [return: NotNullIfNotNull(nameof(plaintext))]
    public string? Protect(string? plaintext) =>
        string.IsNullOrEmpty(plaintext) || _protector is null ? plaintext : _protector.Protect(plaintext);

    [return: NotNullIfNotNull(nameof(stored))]
    public string? Unprotect(string? stored) =>
        string.IsNullOrEmpty(stored) || _protector is null ? stored : _protector.Unprotect(stored);
}
