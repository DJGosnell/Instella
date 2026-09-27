using Instella.Core.Wire;
using Instella.Server.Models;

namespace Instella.Server.Api;

/// <summary>
/// Site-relative API links rendered by the admin UI. Built from the same
/// <see cref="ApiRoutes"/> prefix and <see cref="PlatformMapping"/> names the API uses.
/// </summary>
public static class SiteLinks
{
    /// <summary>Full-build ZIP download.</summary>
    public static string DownloadBuild(string packageId, string version, TargetOS os, Architecture arch) =>
        $"/{ApiRoutes.Prefix}/download/{E(packageId)}/{E(version)}/{PlatformMapping.ToWire(os)}/{PlatformMapping.ToWire(arch)}";

    /// <summary>Patch archive download.</summary>
    public static string Patch(string packageId, string fromVersion, string toVersion, TargetOS os, Architecture arch) =>
        $"/{ApiRoutes.Prefix}/patch/{E(packageId)}/{E(fromVersion)}/{E(toVersion)}/{PlatformMapping.ToWire(os)}/{PlatformMapping.ToWire(arch)}";

    /// <summary>Installer download; <paramref name="version"/> may be <c>latest</c>.</summary>
    public static string Installer(string packageId, string version, TargetOS os, Architecture arch, string kind) =>
        $"/{ApiRoutes.Prefix}/installer/{E(packageId)}/{E(version)}/{PlatformMapping.ToWire(os)}/{PlatformMapping.ToWire(arch)}/{E(kind)}";

    private static string E(string segment) => Uri.EscapeDataString(segment);
}
