using Instella.Core.Wire;
using Instella.Server.Data.Entities;
using Instella.Server.Extensions;
using Instella.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Instella.Server.Auth;

/// <summary>
/// The download access rule for a package, shared by every read endpoint: an open package
/// needs nothing (any credential is ignored); otherwise the request needs a bearer credential.
/// A download token (<c>idt_…</c>) admits <see cref="DownloadAccessMode.PackageKeyRequired"/>
/// packages it was issued for, and nothing else; an API key follows the permission matrix (an
/// admin key for <see cref="DownloadAccessMode.MasterKeyRequired"/>). Every denial is written to
/// the security log.
/// </summary>
/// <remarks>
/// No existence oracle: a request with no or invalid credentials gets exactly what an
/// unknown package gets on that route (<c>unknown</c>: a 404, or check-update's "no update"). Only a
/// valid credential that lacks permission gets 403.
/// </remarks>
public sealed class DownloadAccess(AuthService authService, DownloadTokenService tokens, ISecurityLogService securityLog)
{
    /// <summary>What a route answers for a package it does not know: the default for most routes.</summary>
    public static IActionResult UnknownPackage() => new NotFoundObjectResult(new ApiError { Error = "Package not found" });

    /// <summary>
    /// Null when the request may read <paramref name="package"/>; otherwise what to return:
    /// <paramref name="unknown"/> (default <see cref="UnknownPackage"/>) without valid credentials,
    /// 403 for a valid one that lacks permission.
    /// </summary>
    public async Task<IActionResult?> CheckAsync(HttpContext http, Package package, CancellationToken ct, IActionResult? unknown = null)
    {
        unknown ??= UnknownPackage();
        if (package.DownloadAccessMode == DownloadAccessMode.Open)
            return null;

        var ip = http.GetClientIpAddress();
        var authHeader = http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await securityLog.LogEventAsync(
                SecurityEventType.DownloadDeniedNoKey,
                ip,
                packageId: package.PackageId,
                details: $"Access mode: {package.DownloadAccessMode}",
                ct: ct);
            return unknown;
        }

        var plainKey = authHeader["Bearer ".Length..].Trim();

        if (DownloadTokens.LooksLikeToken(plainKey))
        {
            // MasterKeyRequired never accepts a token (decision "Private packages").
            if (package.DownloadAccessMode == DownloadAccessMode.PackageKeyRequired
                && await tokens.IsValidForAsync(plainKey, package.Id, ct))
                return null;
            await securityLog.LogEventAsync(
                SecurityEventType.DownloadDeniedInvalidKey,
                ip,
                packageId: package.PackageId,
                details: $"Download token {DownloadTokens.DisplayPrefix(plainKey)}…, access mode: {package.DownloadAccessMode}",
                ct: ct);
            return unknown;
        }

        var apiKey = await authService.ValidateApiKeyAsync(plainKey, ct);

        if (apiKey == null)
        {
            await securityLog.LogEventAsync(
                SecurityEventType.DownloadDeniedInvalidKey,
                ip,
                packageId: package.PackageId,
                details: $"Access mode: {package.DownloadAccessMode}",
                ct: ct);
            return unknown;
        }

        // The permission matrix, plus the package's access mode: MasterKeyRequired admits
        // all-package (admin) keys only.
        var permitted = ApiPermissions.Allows(apiKey, package, ApiPermission.Download)
                        && (package.DownloadAccessMode != DownloadAccessMode.MasterKeyRequired || apiKey.Scope == ApiKeyScope.Admin);
        if (!permitted)
        {
            await securityLog.LogEventAsync(
                SecurityEventType.DownloadDeniedInsufficientPermission,
                ip,
                apiKeyName: apiKey.Name,
                packageId: package.PackageId,
                details: $"Key scope: {apiKey.Scope}, CanDownload: {apiKey.CanDownload}, Access mode: {package.DownloadAccessMode}",
                ct: ct);
            return new ObjectResult(new ApiError { Error = "Key lacks download permission for this package" }) { StatusCode = 403 };
        }

        return null;
    }
}
