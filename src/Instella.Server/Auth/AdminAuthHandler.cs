using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace Instella.Server.Auth;

public static class AdminAuthExtensions
{
    public const string CookieScheme = "AdminCookie";

    public static IServiceCollection AddAdminAuthentication(this IServiceCollection services, IWebHostEnvironment environment)
    {
        services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieScheme;
            options.DefaultChallengeScheme = CookieScheme;
        })
        .AddCookie(CookieScheme, options =>
        {
            options.LoginPath = "/login";
            options.LogoutPath = "/logout";
            options.AccessDeniedPath = "/access-denied";
            options.Cookie.Name = "Instella.Admin";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            // Secure always, relaxed only for plain-http local development.
            options.Cookie.SecurePolicy = environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
        })
        .AddApiKeyAuthentication();

        services.AddAuthorization(options =>
        {
            options.AddPolicy("Admin", policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AuthenticationSchemes.Add(CookieScheme);
            });

            options.AddPolicy("ApiKey", policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AuthenticationSchemes.Add(ApiKeyAuthenticationHandler.SchemeName);
            });

            options.AddPolicy("AdminOrApiKey", policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AuthenticationSchemes.Add(CookieScheme);
                policy.AuthenticationSchemes.Add(ApiKeyAuthenticationHandler.SchemeName);
            });
        });

        return services;
    }

    public static async Task SignInAsync(this HttpContext context, long userId, string username, bool requiresTotpVerification = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, username),
            new("RequiresTotp", requiresTotpVerification.ToString())
        };

        var identity = new ClaimsIdentity(claims, CookieScheme);
        var principal = new ClaimsPrincipal(identity);

        await context.SignInAsync(CookieScheme, principal);
    }

    public static async Task SignOutAsync(this HttpContext context)
    {
        await context.SignOutAsync(CookieScheme);
    }

    public static long? GetUserId(this ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier);
        if (claim == null || !long.TryParse(claim.Value, out var id))
            return null;
        return id;
    }

    public static bool RequiresTotpVerification(this ClaimsPrincipal user)
    {
        var claim = user.FindFirst("RequiresTotp");
        return claim?.Value == "True";
    }
}
