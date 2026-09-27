using System.Security.Claims;
using System.Text.Encodings.Web;
using Instella.Server.Data.Entities;
using Instella.Server.Extensions;
using Instella.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Instella.Server.Auth;

public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AuthService authService,
    ISecurityLogService securityLog)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string ApiKeyHeader = "Authorization";
    public const string ApiKeyPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyHeader, out var headerValue))
            return AuthenticateResult.NoResult();

        var header = headerValue.ToString();
        if (!header.StartsWith(ApiKeyPrefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var apiKey = header[ApiKeyPrefix.Length..].Trim();
        if (string.IsNullOrEmpty(apiKey))
            return AuthenticateResult.Fail("Empty API key");

        var key = await authService.ValidateApiKeyAsync(apiKey);
        if (key == null)
        {
            var ip = Context.GetClientIpAddress();
            await securityLog.LogEventAsync(
                SecurityEventType.ApiKeyInvalid,
                ip,
                details: "API key not found or revoked");
            return AuthenticateResult.Fail("Invalid API key");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, key.Id.ToString()),
            new(ClaimTypes.Name, key.Name),
            new("ApiKeyScope", key.Scope.ToString()),
        };

        if (key.PackageId.HasValue)
            claims.Add(new Claim("PackageId", key.PackageId.Value.ToString()));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }
}

public static class ApiKeyAuthExtensions
{
    public static AuthenticationBuilder AddApiKeyAuthentication(this AuthenticationBuilder builder)
    {
        return builder.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
            ApiKeyAuthenticationHandler.SchemeName,
            _ => { });
    }
}
