using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Instella.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;

namespace Instella.Contract.Tests;

/// <summary>
/// The real Instella server, hosted in-process against a temp-file SQLite database and
/// a temp blob directory. The SQLite file (not the EF in-memory provider) matters: it
/// enforces foreign keys and transactions exactly as production does.
/// </summary>
public sealed class ContractServer : WebApplicationFactory<Instella.Server.Api.PackagesController>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"instella-contract-{Guid.NewGuid():N}");

    /// <summary>
    /// The connection address every in-process client appears to come from (replacing TestServer's
    /// default), so address-based rules such as bans and forwarded-header handling are exercised.
    /// </summary>
    public static readonly System.Net.IPAddress ClientAddress = System.Net.IPAddress.Parse("198.51.100.7");

    /// <summary>The address this server's clients connect from: <see cref="ClientAddress"/> unless given.</summary>
    public System.Net.IPAddress Address { get; }

    /// <param name="clientAddress">The connection address of every client; default <see cref="ClientAddress"/>.</param>
    public ContractServer(System.Net.IPAddress? clientAddress = null) => Address = clientAddress ?? ClientAddress;

    /// <summary>The server's config directory (database, Data Protection keys, setup token).</summary>
    public string ConfigDir => _root;

    /// <summary>Base URL the in-process clients use.</summary>
    public string BaseUrl => "http://localhost/";

    /// <summary>On-disk path of a content blob in the server's local storage.</summary>
    public string BlobPath(string sha256) =>
        Path.Combine(_root, "packages", sha256[..2], sha256[2..4], sha256);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_root);
        builder.UseEnvironment("Development");
        // Data Protection keys and the setup token live in the config dir; keep them out of the source tree.
        builder.UseSetting("Instella:ConfigDir", _root);
        builder.UseSetting("Database:ConnectionString", $"Data Source={Path.Combine(_root, "instella.db")};Pooling=False");
        builder.UseSetting("Storage:Local:BasePath", Path.Combine(_root, "packages"));
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new ClientAddressFilter(Address)));
    }

    /// <summary>Creates an open-download package and an admin-scope API key that can upload and download.</summary>
    public async Task<string> SeedPackageAndKeyAsync(string packageId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Packages.Add(new Package
        {
            PackageId = packageId,
            DisplayName = packageId,
            DownloadAccessMode = DownloadAccessMode.Open,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var (_, plainKey) = await auth.CreateApiKeyAsync("contract", ApiKeyScope.Admin, canUpload: true, canDownload: true,
            canManageVersions: true);
        return plainKey;
    }

    /// <summary>Runs <paramref name="query"/> against a fresh DbContext.</summary>
    public async Task<T> QueryAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>
    /// Resolves <paramref name="uri"/> against the server's controller routes the way
    /// routing would, returning the matched action name and route values, or null.
    /// </summary>
    public (string Action, RouteValueDictionary Values)? MatchRoute(string method, Uri uri)
    {
        var path = PathString.FromUriComponent(uri);
        var endpoints = Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        foreach (var endpoint in endpoints)
        {
            var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
            if (action is null) continue;
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            if (methods is null || !methods.Contains(method, StringComparer.OrdinalIgnoreCase)) continue;

            var matcher = new TemplateMatcher(TemplateParser.Parse(endpoint.RoutePattern.RawText!), new RouteValueDictionary());
            var values = new RouteValueDictionary();
            if (matcher.TryMatch(path, values))
                return (action.ActionName, values);
        }
        return null;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* best effort; SQLite may still hold the file briefly */ }
        }
    }
}

/// <summary>Sets the connection address before any of the app's middleware runs.</summary>
internal sealed class ClientAddressFilter(System.Net.IPAddress address) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = address;
            return nextMiddleware(context);
        });
        next(app);
    };
}
