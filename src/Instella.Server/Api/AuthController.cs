using Instella.Server.Auth;
using Instella.Server.Data.Entities;
using Instella.Server.Extensions;
using Instella.Server.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Instella.Server.Api;

/// <summary>
/// Admin sign-in form posts. These are admin-UI routes, not API contract, so they stay outside
/// <c>ApiRoutes</c>; every one validates an antiforgery token.
/// </summary>
[Route("api/auth")]
[RejectInvalidAntiforgery]
[ApiController]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(AuthRateLimit.Policy)]
public class AuthController(
    AuthService authService,
    IRateLimitService rateLimitService,
    ISecurityLogService securityLog,
    PendingSecondFactorStore pending,
    IWebHostEnvironment environment) : ControllerBase
{
    /// <summary>
    /// The per-address key: 20 failures from one address, across any usernames, start
    /// the same backoff as <c>login:{user}@{ip}</c>, so guessing many usernames is slowed too.
    /// </summary>
    private static string LoginIpKey(string clientIp) => $"login-ip:{clientIp}";

    private (bool Allowed, int DelaySeconds) Check(string key, string ipKey)
    {
        var (userAllowed, userDelay) = rateLimitService.CheckRequest(key);
        var (ipAllowed, ipDelay) = rateLimitService.CheckRequest(ipKey);
        return (userAllowed && ipAllowed, Math.Max(userDelay, ipDelay));
    }

    private void RecordFailure(string key, string ipKey)
    {
        rateLimitService.RecordFailure(key);
        rateLimitService.RecordFailure(ipKey, freeFailures: RateLimitConfig.LoginFailuresPerIp - 1);
    }

    [HttpPost("login")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> Login([FromForm] LoginRequest request)
    {
        var clientIp = HttpContext.GetClientIpAddress();
        var key = $"login:{request.Username}@{clientIp}";
        var ipKey = LoginIpKey(clientIp);

        var (allowed, delay) = Check(key, ipKey);
        if (!allowed)
        {
            await securityLog.LogEventAsync(SecurityEventType.LoginBlocked, clientIp, request.Username);
            return Redirect($"/login?error=ratelimit&delay={delay}");
        }

        var user = await authService.ValidateCredentialsAsync(request.Username, request.Password);
        if (user == null)
        {
            RecordFailure(key, ipKey);
            await securityLog.LogEventAsync(SecurityEventType.LoginFailed, clientIp, request.Username);
            return Redirect("/login?error=invalid");
        }

        if (user.TotpEnabled)
        {
            // The only way to reach the TOTP step: a server-issued, short-lived ticket bound
            // to this password step. No user id ever travels in the URL or the form.
            Response.Cookies.Append(PendingSecondFactorStore.CookieName, pending.Issue(user.Id), new CookieOptions
            {
                HttpOnly = true,
                Secure = SecureCookies,
                SameSite = SameSiteMode.Strict,
                MaxAge = PendingSecondFactorStore.Lifetime,
                Path = "/",
            });
            return Redirect("/login?step=totp");
        }

        rateLimitService.RecordSuccess(key);
        rateLimitService.RecordSuccess(ipKey);
        await securityLog.LogEventAsync(SecurityEventType.LoginSuccess, clientIp, request.Username);
        await HttpContext.SignInAsync(user.Id, user.Username);
        return Redirect("/");
    }

    [HttpPost("totp")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> VerifyTotp([FromForm] string code)
    {
        var clientIp = HttpContext.GetClientIpAddress();
        if (!pending.TryRead(Request.Cookies[PendingSecondFactorStore.CookieName], out var ticket))
            return Redirect("/login?error=expired");

        var user = await authService.GetAdminUserAsync(ticket.UserId);
        if (user is null || !user.TotpEnabled)
            return Redirect("/login?error=invalid");

        var key = $"login:{user.Username}@{clientIp}";
        var ipKey = LoginIpKey(clientIp);
        var (allowed, delay) = Check(key, ipKey);
        if (!allowed || !pending.TryConsumeAttempt(ticket.Nonce))
        {
            await securityLog.LogEventAsync(SecurityEventType.LoginBlocked, clientIp, user.Username, details: "TOTP attempts exhausted");
            return Redirect($"/login?error=ratelimit&delay={delay}");
        }

        if (!await authService.ValidateTotpAndAdvanceStepAsync(user, code))
        {
            RecordFailure(key, ipKey);
            await securityLog.LogEventAsync(SecurityEventType.LoginFailed, clientIp, user.Username, details: "Invalid TOTP code");
            return Redirect("/login?step=totp&error=totp");
        }

        Response.Cookies.Delete(PendingSecondFactorStore.CookieName);
        rateLimitService.RecordSuccess(key);
        rateLimitService.RecordSuccess(ipKey);
        await securityLog.LogEventAsync(SecurityEventType.LoginSuccess, clientIp, user.Username);
        await HttpContext.SignInAsync(user.Id, user.Username);
        return Redirect("/");
    }

    /// <summary>POST with an antiforgery token, so a third-party page cannot sign the admin out.</summary>
    [HttpPost("logout")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync();
        return Redirect("/login");
    }

    /// <summary>Secure cookies everywhere except plain-http development.</summary>
    private bool SecureCookies => Request.IsHttps || !environment.IsDevelopment();
}

public class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}
