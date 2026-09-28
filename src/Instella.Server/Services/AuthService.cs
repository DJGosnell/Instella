using System.Security.Cryptography;
using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OtpNet;

namespace Instella.Server.Services;

/// <param name="secrets">Protects TOTP secrets at rest; null stores them as given (unit tests).</param>
public class AuthService(AppDbContext db, SecretProtector? secrets = null)
{
    private SecretProtector Secrets => secrets ?? SecretProtector.None;

    private readonly PasswordHasher<AdminUser> _passwordHasher = new();

    // Setup check
    public async Task<bool> IsSetupCompleteAsync(CancellationToken ct = default)
    {
        return await db.AdminUsers.AnyAsync(ct);
    }

    // Admin user management
    public async Task<AdminUser> CreateAdminUserAsync(string username, string password, CancellationToken ct = default)
    {
        var user = new AdminUser
        {
            Username = username,
            PasswordHash = ""
        };
        user.PasswordHash = _passwordHasher.HashPassword(user, password);

        db.AdminUsers.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<AdminUser?> ValidateCredentialsAsync(string username, string password, CancellationToken ct = default)
    {
        var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Username == username, ct);
        if (user == null) return null;

        var result = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
            return null;

        // Rehash if needed (security upgrade)
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _passwordHasher.HashPassword(user, password);
            await db.SaveChangesAsync(ct);
        }

        return user;
    }

    public async Task<AdminUser?> GetAdminUserAsync(long id, CancellationToken ct = default)
    {
        return await db.AdminUsers.FindAsync([id], ct);
    }

    public async Task<bool> ChangePasswordAsync(long userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await db.AdminUsers.FindAsync([userId], ct);
        if (user == null) return false;

        var result = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword);
        if (result == PasswordVerificationResult.Failed)
            return false;

        user.PasswordHash = _passwordHasher.HashPassword(user, newPassword);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // TOTP management
    public async Task<string> SetupTotpAsync(long userId, CancellationToken ct = default)
    {
        var user = await db.AdminUsers.FindAsync([userId], ct);
        if (user == null) throw new InvalidOperationException("User not found");

        // Generate a new secret
        var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
        user.TotpSecret = Secrets.Protect(secret);
        user.TotpEnabled = false; // Not enabled until verified
        await db.SaveChangesAsync(ct);

        return secret;
    }

    public async Task<bool> VerifyAndEnableTotpAsync(long userId, string code, CancellationToken ct = default)
    {
        var user = await db.AdminUsers.FindAsync([userId], ct);
        if (user == null || user.TotpSecret == null) return false;

        var totp = new Totp(Base32Encoding.ToBytes(Secrets.Unprotect(user.TotpSecret)));

        if (!totp.VerifyTotp(code, out var timeStep, VerificationWindow.RfcSpecifiedNetworkDelay))
            return false;

        user.TotpEnabled = true;
        user.LastTotpTimeStep = timeStep;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Verifies a TOTP code for the second sign-in step and records its time step. A code whose
    /// step is not later than the last accepted one is refused (replay protection).
    /// </summary>
    public async Task<bool> ValidateTotpAndAdvanceStepAsync(AdminUser user, string code, CancellationToken ct = default)
    {
        if (!user.TotpEnabled || user.TotpSecret == null || string.IsNullOrWhiteSpace(code)) return false;

        var totp = new Totp(Base32Encoding.ToBytes(Secrets.Unprotect(user.TotpSecret)));
        if (!totp.VerifyTotp(code.Trim(), out var timeStep, VerificationWindow.RfcSpecifiedNetworkDelay))
            return false;
        if (timeStep <= user.LastTotpTimeStep)
            return false;

        user.LastTotpTimeStep = timeStep;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DisableTotpAsync(long userId, CancellationToken ct = default)
    {
        var user = await db.AdminUsers.FindAsync([userId], ct);
        if (user == null) return false;

        user.TotpEnabled = false;
        user.TotpSecret = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public string GenerateTotpUri(string secret, string username, string issuer = "Instella")
    {
        var secretBytes = Base32Encoding.ToBytes(secret);
        var totp = new Totp(secretBytes);
        return new OtpUri(OtpType.Totp, secretBytes, username, issuer).ToString();
    }

    // API Key management

    /// <summary>Creates an API key; the plain key is returned once and only its hash is stored.</summary>
    /// <exception cref="ArgumentException">
    /// Both <paramref name="canUpload"/> and <paramref name="canApproveReleases"/>: an approve key belongs
    /// to a person, and a CI key that could approve would approve its own uploads.
    /// </exception>
    public async Task<(ApiKey key, string plainKey)> CreateApiKeyAsync(
        string name,
        ApiKeyScope scope,
        bool canUpload = true,
        bool canDownload = false,
        long? packageId = null,
        bool canManageVersions = false,
        bool canApproveReleases = false,
        CancellationToken ct = default)
    {
        if (canUpload && canApproveReleases)
            throw new ArgumentException("A key cannot both upload and approve releases: give CI an upload key and the approver a separate approve key.");

        // Generate a random API key (512-bit for enhanced security)
        var keyBytes = RandomNumberGenerator.GetBytes(64);
        var plainKey = Convert.ToBase64String(keyBytes);

        // Hash the key for storage
        var keyHash = ComputeKeyHash(plainKey);

        var apiKey = new ApiKey
        {
            KeyHash = keyHash,
            Name = name,
            Scope = scope,
            PackageId = packageId,
            CanUpload = canUpload,
            CanDownload = canDownload,
            CanManageVersions = canManageVersions,
            CanApproveReleases = canApproveReleases,
        };

        db.ApiKeys.Add(apiKey);
        await db.SaveChangesAsync(ct);

        return (apiKey, plainKey);
    }

    public async Task<ApiKey?> ValidateApiKeyAsync(string plainKey, CancellationToken ct = default)
    {
        var keyHash = ComputeKeyHash(plainKey);
        var apiKey = await db.ApiKeys
            .Include(k => k.Package)
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash && !k.IsRevoked, ct);

        if (apiKey != null)
        {
            apiKey.LastUsedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return apiKey;
    }

    public async Task<List<ApiKey>> GetApiKeysAsync(CancellationToken ct = default)
    {
        return await db.ApiKeys
            .Include(k => k.Package)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<bool> RevokeApiKeyAsync(long id, CancellationToken ct = default)
    {
        var apiKey = await db.ApiKeys.FindAsync([id], ct);
        if (apiKey == null) return false;

        apiKey.IsRevoked = true;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteApiKeyAsync(long id, CancellationToken ct = default)
    {
        var apiKey = await db.ApiKeys.FindAsync([id], ct);
        if (apiKey == null) return false;

        db.ApiKeys.Remove(apiKey);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string ComputeKeyHash(string plainKey)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plainKey));
        return Convert.ToHexStringLower(hash);
    }
}
