using Instella.Core.Platform;

namespace Instella.Core.Wire;

/// <summary>
/// The single source of truth for the Instella HTTP API surface (v1). The server uses
/// the template constants in its routing attributes; clients use the builder methods.
/// </summary>
public static class ApiRoutes
{
    /// <summary>Prefix shared by every API route.</summary>
    public const string Prefix = "api/v1";

    // Templates (server attributes), relative to Prefix. Attribute arguments must be
    // compile-time constants, hence const strings.

    /// <summary>GET: is a newer version available?</summary>
    public const string CheckUpdate = "check-update";

    /// <summary>GET: the signed release manifest of one build.</summary>
    public const string Release = "release/{packageId}/{version}/{os}/{arch}";

    /// <summary>GET: a full build as a ZIP archive.</summary>
    public const string DownloadBuild = "download/{packageId}/{version}/{os}/{arch}";

    /// <summary>GET: one file of a build.</summary>
    public const string DownloadFile = "download/{packageId}/{version}/{os}/{arch}/file/{**path}";

    /// <summary>GET: the patch archive between two builds.</summary>
    public const string Patch = "patch/{packageId}/{fromVersion}/{toVersion}/{os}/{arch}";

    /// <summary>GET: the patch manifest between two builds.</summary>
    public const string PatchManifest = "patch-manifest/{packageId}/{fromVersion}/{toVersion}/{os}/{arch}";

    /// <summary>GET: open-access package list.</summary>
    public const string Packages = "packages";

    /// <summary>GET: one package (a private one needs an API key that may download it).</summary>
    public const string Package = "packages/{packageId}";

    /// <summary>GET: versions of a package (a private one needs an API key that may download it).</summary>
    public const string PackageVersions = "packages/{packageId}/versions";

    /// <summary>PUT/DELETE: one version of a package.</summary>
    public const string PackageVersion = "packages/{packageId}/versions/{version}";

    /// <summary>POST: start an upload session.</summary>
    public const string UploadStart = "upload/start";

    /// <summary>POST: upload one file into a session.</summary>
    public const string UploadFile = "upload/{sessionId}/file";

    /// <summary>POST: complete an upload session.</summary>
    public const string UploadComplete = "upload/{sessionId}/complete";

    /// <summary>DELETE: cancel an upload session.</summary>
    public const string UploadCancel = "upload/{sessionId}";

    /// <summary>POST: upload an installer (<c>?kind=&amp;fileName=&amp;sha256=</c>) into a session.</summary>
    public const string UploadInstaller = "upload/{sessionId}/installer";

    /// <summary>
    /// GET: an installer of one build; <c>{version}</c> may be <see cref="LatestVersion"/>
    /// (newest non-deprecated version on <c>?channel=</c>, default stable).
    /// </summary>
    public const string DownloadInstaller = "installer/{packageId}/{version}/{os}/{arch}/{kind}";

    /// <summary>GET: an unpublished (draft) build's unsigned release manifest (upload permission).</summary>
    public const string Draft = "drafts/{packageId}/{version}/{os}/{arch}";

    /// <summary>POST: publish a draft build with its signed release (upload permission).</summary>
    public const string PublishDraft = "drafts/{packageId}/{version}/{os}/{arch}/publish";

    /// <summary>The <c>{version}</c> of <see cref="DownloadInstaller"/> that means the newest one.</summary>
    public const string LatestVersion = "latest";

    // Builders (clients). Every segment is escaped; file paths keep their '/' separators.

    /// <summary>URL of <see cref="CheckUpdate"/>.</summary>
    public static Uri ForCheckUpdate(Uri server, string packageId, Version current, TargetPlatform os,
        Architecture arch, string channel) =>
        Build(server, CheckUpdate, new()
        {
            ["packageId"] = packageId,
            ["currentVersion"] = current.ToString(),
            ["os"] = PlatformStrings.Os(os),
            ["arch"] = PlatformStrings.Arch(arch),
            ["channel"] = channel,
        });

    /// <summary>URL of <see cref="Release"/>.</summary>
    public static Uri ForRelease(Uri server, string packageId, Version version, TargetPlatform os, Architecture arch) =>
        Build(server, $"release/{Build(packageId, version, os, arch)}");

    /// <summary>URL of <see cref="DownloadBuild"/>.</summary>
    public static Uri ForDownloadBuild(Uri server, string packageId, Version version, TargetPlatform os, Architecture arch) =>
        Build(server, $"download/{Build(packageId, version, os, arch)}");

    /// <summary>URL of <see cref="DownloadFile"/>.</summary>
    public static Uri ForDownloadFile(Uri server, string packageId, Version version, TargetPlatform os,
        Architecture arch, string relativePath) =>
        Build(server, $"download/{Build(packageId, version, os, arch)}/file/{EPath(relativePath)}");

    /// <summary>URL of <see cref="Patch"/>.</summary>
    public static Uri ForPatch(Uri server, string packageId, Version from, Version to, TargetPlatform os, Architecture arch) =>
        Build(server, $"patch/{E(packageId)}/{E(from.ToString())}/{E(to.ToString())}/{PlatformStrings.Os(os)}/{PlatformStrings.Arch(arch)}");

    /// <summary>URL of <see cref="PatchManifest"/>.</summary>
    public static Uri ForPatchManifest(Uri server, string packageId, Version from, Version to, TargetPlatform os, Architecture arch) =>
        Build(server, $"patch-manifest/{E(packageId)}/{E(from.ToString())}/{E(to.ToString())}/{PlatformStrings.Os(os)}/{PlatformStrings.Arch(arch)}");

    /// <summary>URL of <see cref="Packages"/>.</summary>
    public static Uri ForPackages(Uri server) => Build(server, Packages);

    /// <summary>URL of <see cref="Package"/>.</summary>
    public static Uri ForPackage(Uri server, string packageId) => Build(server, $"packages/{E(packageId)}");

    /// <summary>URL of <see cref="PackageVersions"/>.</summary>
    public static Uri ForPackageVersions(Uri server, string packageId) =>
        Build(server, $"packages/{E(packageId)}/versions");

    /// <summary>URL of <see cref="PackageVersion"/>.</summary>
    public static Uri ForPackageVersion(Uri server, string packageId, string version) =>
        Build(server, $"packages/{E(packageId)}/versions/{E(version)}");

    /// <summary>URL of <see cref="UploadStart"/>.</summary>
    public static Uri ForUploadStart(Uri server) => Build(server, UploadStart);

    /// <summary>URL of <see cref="UploadFile"/>.</summary>
    public static Uri ForUploadFile(Uri server, Guid sessionId, string relativePath, string sha256) =>
        Build(server, $"upload/{sessionId:D}/file", new()
        {
            ["path"] = relativePath.Replace('\\', '/'),
            ["sha256"] = sha256,
        });

    /// <summary>URL of <see cref="UploadComplete"/>.</summary>
    public static Uri ForUploadComplete(Uri server, Guid sessionId) => Build(server, $"upload/{sessionId:D}/complete");

    /// <summary>URL of <see cref="UploadCancel"/>.</summary>
    public static Uri ForUploadCancel(Uri server, Guid sessionId) => Build(server, $"upload/{sessionId:D}");

    /// <summary>URL of <see cref="UploadInstaller"/>.</summary>
    public static Uri ForUploadInstaller(Uri server, Guid sessionId, string kind, string fileName, string sha256) =>
        Build(server, $"upload/{sessionId:D}/installer", new()
        {
            ["kind"] = kind,
            ["fileName"] = fileName,
            ["sha256"] = sha256,
        });

    /// <summary>URL of <see cref="DownloadInstaller"/> for one version.</summary>
    public static Uri ForDownloadInstaller(Uri server, string packageId, Version version, TargetPlatform os,
        Architecture arch, string kind) =>
        Build(server, $"installer/{Build(packageId, version, os, arch)}/{E(kind)}");

    /// <summary>URL of <see cref="DownloadInstaller"/> for the newest version on <paramref name="channel"/>.</summary>
    public static Uri ForLatestInstaller(Uri server, string packageId, TargetPlatform os, Architecture arch,
        string kind, string? channel = null) =>
        Build(server, $"installer/{E(packageId)}/{LatestVersion}/{PlatformStrings.Os(os)}/{PlatformStrings.Arch(arch)}/{E(kind)}",
            channel is null ? null : new() { ["channel"] = channel });

    /// <summary>URL of <see cref="Draft"/>.</summary>
    public static Uri ForDraft(Uri server, string packageId, Version version, TargetPlatform os, Architecture arch) =>
        Build(server, $"drafts/{Build(packageId, version, os, arch)}");

    /// <summary>URL of <see cref="PublishDraft"/>.</summary>
    public static Uri ForPublishDraft(Uri server, string packageId, Version version, TargetPlatform os, Architecture arch) =>
        Build(server, $"drafts/{Build(packageId, version, os, arch)}/publish");

    private static string Build(string packageId, Version version, TargetPlatform os, Architecture arch) =>
        $"{E(packageId)}/{E(version.ToString())}/{PlatformStrings.Os(os)}/{PlatformStrings.Arch(arch)}";

    private static string E(string segment) => Uri.EscapeDataString(segment);

    private static string EPath(string path) =>
        string.Join('/', path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));

    private static Uri Build(Uri server, string relative, Dictionary<string, string>? query = null)
    {
        var baseUri = server.AbsoluteUri.EndsWith('/') ? server : new Uri(server.AbsoluteUri + "/");
        var uri = new Uri(baseUri, $"{Prefix}/{relative}");
        if (query is null) return uri;
        var qs = string.Join('&', query.Select(kv => $"{E(kv.Key)}={E(kv.Value)}"));
        return new UriBuilder(uri) { Query = qs }.Uri;
    }
}
