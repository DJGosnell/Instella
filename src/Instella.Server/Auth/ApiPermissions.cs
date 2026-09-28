using Instella.Server.Data.Entities;

namespace Instella.Server.Auth;

/// <summary>What an API call needs from the calling key.</summary>
public enum ApiPermission
{
    /// <summary><c>upload/*</c>.</summary>
    Upload,

    /// <summary>Editing, deprecating or deleting versions: keys with <see cref="ApiKey.CanManageVersions"/>.</summary>
    ManageVersions,

    /// <summary><c>download/*</c>, <c>patch*</c>, <c>release/*</c> and <c>check-update</c> of a private package.</summary>
    Download,

    /// <summary>
    /// Approving or rejecting releases held by the package's release approval: keys with
    /// <see cref="ApiKey.CanApproveReleases"/>.
    /// </summary>
    ApproveReleases,
}

/// <summary>The single permission matrix for API keys; every API check goes through it.</summary>
public static class ApiPermissions
{
    /// <summary>
    /// True when <paramref name="key"/> may perform <paramref name="permission"/> on
    /// <paramref name="package"/>: not revoked, scoped to all packages or to this one, and
    /// carrying the matching capability flag.
    /// </summary>
    public static bool Allows(ApiKey key, Package package, ApiPermission permission) =>
        !key.IsRevoked
        && (key.Scope == ApiKeyScope.Admin || key.PackageId == package.Id)
        && permission switch
        {
            ApiPermission.Upload => key.CanUpload,
            ApiPermission.ManageVersions => key.CanManageVersions,
            ApiPermission.Download => key.CanDownload,
            ApiPermission.ApproveReleases => key.CanApproveReleases,
            _ => false,
        };
}
