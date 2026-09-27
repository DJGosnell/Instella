using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;

namespace Instella.Server.Auth;

/// <summary>A password step that succeeded for a TOTP-enabled user.</summary>
public sealed record PendingSecondFactorTicket(long UserId, string Nonce);

/// <summary>
/// Binds the TOTP step to a completed password step. Only <c>Login</c> can issue a ticket; it
/// travels as a data-protected, 5-minute HttpOnly cookie, so the TOTP endpoint never takes a
/// user id from the form. Attempts are limited per ticket.
/// </summary>
public sealed class PendingSecondFactorStore(IDataProtectionProvider dataProtection, IMemoryCache cache)
{
    public const string CookieName = "Instella.Pending2fa";
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ITimeLimitedDataProtector _protector =
        dataProtection.CreateProtector("Instella.Auth.Pending2fa.v1").ToTimeLimitedDataProtector();

    /// <summary>A protected ticket value for <paramref name="userId"/>.</summary>
    public string Issue(long userId)
    {
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        return _protector.Protect($"{userId}|{nonce}", Lifetime);
    }

    /// <summary>False for a missing, expired or tampered ticket.</summary>
    public bool TryRead(string? raw, out PendingSecondFactorTicket ticket)
    {
        ticket = null!;
        if (string.IsNullOrEmpty(raw)) return false;
        try
        {
            var parts = _protector.Unprotect(raw).Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var userId)) return false;
            ticket = new PendingSecondFactorTicket(userId, parts[1]);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Counts one TOTP attempt against the ticket; false once <see cref="MaxAttempts"/> are used.</summary>
    public bool TryConsumeAttempt(string nonce)
    {
        var key = "pending2fa:" + nonce;
        var used = cache.GetOrCreate(key, e =>
        {
            e.AbsoluteExpirationRelativeToNow = Lifetime;
            return new Counter();
        })!;
        return Interlocked.Increment(ref used.Value) <= MaxAttempts;
    }

    private sealed class Counter
    {
        public int Value;
    }
}
