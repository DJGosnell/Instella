using Instella.Server.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Instella.Server.Services;

/// <summary><c>/healthz</c>: healthy when the database answers. Cheap, unlike listing packages.</summary>
public sealed class DatabaseHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("database unreachable");
}

/// <summary>The rate-limiter policy on anonymous download endpoints.</summary>
public static class DownloadRateLimit
{
    public const string Policy = "downloads";
}

/// <summary>The rate-limiter policy on the package API (<c>PackagesController</c>).</summary>
public static class ApiRateLimit
{
    public const string Policy = "api";
}

/// <summary>The rate-limiter policy on sign-in (<c>AuthController</c>), on top of the login backoff.</summary>
public static class AuthRateLimit
{
    public const string Policy = "auth";
}
