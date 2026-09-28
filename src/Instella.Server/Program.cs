using System.Net;
using System.Threading.RateLimiting;
using Instella.Server;
using Instella.Server.Auth;
using Instella.Server.Data;
using Instella.Server.Extensions;
using Instella.Server.Services;
using Instella.Server.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseWebRoot("wwwroot");

// ---- Configuration: one well-known config directory ----------------------------
// Database, Data Protection keys and the setup token live there. In the container it is the
// /config volume; INSTELLA_CONFIG_DIR (or the Instella:ConfigDir setting) overrides it.
var configDir = builder.Configuration["Instella:ConfigDir"]
    ?? Environment.GetEnvironmentVariable("INSTELLA_CONFIG_DIR")
    ?? (OperatingSystem.IsLinux() && Directory.Exists("/config") ? "/config" : builder.Environment.ContentRootPath);
Directory.CreateDirectory(configDir);
// It holds the database, the Data Protection keys and the setup token: owner-only (0700) off
// Windows, best effort (a host bind mount may not allow it). When the directory is the content
// root (development) it is left alone.
var configIsContentRoot = string.Equals(Path.GetFullPath(configDir).TrimEnd(Path.DirectorySeparatorChar),
    Path.GetFullPath(builder.Environment.ContentRootPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
if (!configIsContentRoot)
{
    foreach (var dir in new[] { configDir, Directory.CreateDirectory(Path.Combine(configDir, "keys")).FullName })
        if (ConfigDirectory.RestrictToOwner(dir) is { } permissionWarning)
            Console.Error.WriteLine(permissionWarning);   // before logging exists; not fatal
}
if (!configIsContentRoot)
    builder.Configuration.AddJsonFile(Path.Combine(configDir, "appsettings.json"), optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables("INSTELLA_");   // e.g. INSTELLA_Database__ConnectionString

var connectionString = builder.Configuration["Database:ConnectionString"]
    ?? $"Data Source={Path.Combine(configDir, "instella.db")}";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

// ---- UI and API ------------------------------------------------------------------------------
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, Instella.Core.Wire.WireJsonContext.Default));
builder.Services.AddProblemDetails();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMemoryCache();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

// ---- Security ---------------------------------------------------------------------------------
// Forwarded headers are trusted only from configured proxies. With none configured the
// middleware is not registered at all: ForwardedHeadersMiddleware treats *empty* KnownProxies and
// KnownIPNetworks lists as "trust every peer", so clearing them would enable spoofing.
var knownProxies = builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
var knownNetworks = builder.Configuration.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? [];
var trustsProxies = knownProxies.Length + knownNetworks.Length > 0;
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    o.KnownProxies.Clear();
    o.KnownIPNetworks.Clear();
    foreach (var ip in knownProxies)
        o.KnownProxies.Add(IPAddress.Parse(ip));
    foreach (var cidr in knownNetworks)
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
});

// Data Protection keys persist in the config directory, so auth cookies, pending-2FA
// tickets and protected secrets survive restarts. On Linux the directory's permissions are the
// protection (0700, set above, owned by the service user); on Windows the keys are also DPAPI-protected.
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("Instella.Server")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(configDir, "keys")));
if (OperatingSystem.IsWindows())
    dataProtection.ProtectKeysWithDpapi();

builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<PendingSecondFactorStore>();
builder.Services.AddSingleton(sp => new SetupTokenService(configDir, sp.GetRequiredService<ILogger<SetupTokenService>>()));
builder.Services.AddAdminAuthentication(builder.Environment);

// Every public route is rate limited per client address: downloads, the package API
// and sign-in, each with its own budget.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    void PerClient(string policy, int perMinute) =>
        o.AddPolicy(policy, context => RateLimitPartition.GetFixedWindowLimiter(
            context.GetClientIpAddress(),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) }));
    PerClient(DownloadRateLimit.Policy, builder.Configuration.GetValue("Downloads:PermitsPerMinute", 600));
    PerClient(ApiRateLimit.Policy, builder.Configuration.GetValue("Api:PermitsPerMinute", 300));
    PerClient(AuthRateLimit.Policy, builder.Configuration.GetValue("Auth:PermitsPerMinute", 20));
});

// ---- Storage and services -------------------------------------------------------------------
// The provider is rebuilt from the saved settings after they change; no restart needed.
builder.Services.AddSingleton<IStorageProviderAccessor, StorageProviderAccessor>();
builder.Services.AddTransient<IStorageProvider>(sp => sp.GetRequiredService<IStorageProviderAccessor>().Current);
builder.Services.Configure<UploadLimits>(builder.Configuration.GetSection("Upload"));

builder.Services.AddScoped<PackageService>();
builder.Services.AddScoped<DownloadPageService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<DownloadAccess>();
builder.Services.AddScoped<DownloadTokenService>();
builder.Services.AddScoped<DiffService>();
builder.Services.AddSingleton(new ServerConfigDirectory(configDir));
builder.Services.AddScoped<StorageSettingsService>();
builder.Services.AddScoped<ContentStorageService>();
builder.Services.AddScoped<UploadService>();
builder.Services.AddSingleton<SecurityEventThrottle>();
builder.Services.AddScoped<ISecurityLogService, SecurityLogService>();
builder.Services.AddScoped<IIpBanService, IpBanService>();
builder.Services.AddSingleton<IpBanCache>();
builder.Services.AddSingleton<IRateLimitService, RateLimitService>();
builder.Services.AddScoped<ReleaseApprovalService>();
builder.Services.AddHostedService<PatchJobWorker>();
builder.Services.AddHostedService<DelayedReleaseWorker>();
builder.Services.AddHostedService<OrphanSweeper>();

var app = builder.Build();
if (trustsProxies)
    app.UseForwardedHeaders();   // first: everything after sees the real client address and scheme

// Permanent IP bans (admin UI) cover every endpoint, UI and API alike; /healthz stays open so
// the container health check never depends on the ban list.
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/healthz")
        && await context.RequestServices.GetRequiredService<IpBanCache>()
            .IsBannedAsync(Instella.Server.Extensions.HttpContextExtensions.GetClientIpAddress(context), context.RequestAborted))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        if (context.Request.Path.StartsWithSegments("/api"))
            await context.Response.WriteAsJsonAsync(new Instella.Core.Wire.ApiError { Error = "Access denied." });
        else
            await context.Response.WriteAsync("Access denied.");
        return;
    }
    await next();
});

// Schema: migrations only. Every schema change ships as a migration, so an existing
// database upgrades in place at startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    if (!await scope.ServiceProvider.GetRequiredService<AuthService>().IsSetupCompleteAsync())
        scope.ServiceProvider.GetRequiredService<SetupTokenService>().EnsureToken();
}

// Errors: API callers get RFC 7807 ProblemDetails with a traceId and no exception
// details; the UI re-executes to /Error. Development keeps the detailed exception page.
if (!app.Environment.IsDevelopment())
{
    app.UseWhen(c => c.Request.Path.StartsWithSegments("/api"), api => api.UseExceptionHandler());
    app.UseWhen(c => !c.Request.Path.StartsWithSegments("/api"), ui => ui.UseExceptionHandler("/Error", createScopeForErrors: true));
}
app.UseWhen(c => !c.Request.Path.StartsWithSegments("/api"), ui => ui.UseStatusCodePagesWithReExecute("/not-found"));

// Transport: HTTPS and HSTS unless a proxy terminates TLS (X-Forwarded-Proto supplies the scheme).
if (!app.Configuration.GetValue<bool>("Server:TlsTerminatedByProxy"))
{
    if (!app.Environment.IsDevelopment()) app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapHealthChecks("/healthz", new HealthCheckOptions()).AllowAnonymous();
app.MapControllers();
app.MapRazorComponents<Instella.Server.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Entry point; public so integration tests can host the app.</summary>
public partial class Program;
