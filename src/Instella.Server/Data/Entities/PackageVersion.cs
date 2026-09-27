using Instella.Core.Utilities;
using Instella.Core.Wire;

namespace Instella.Server.Data.Entities;

/// <summary>
/// Represents a version of a package.
/// </summary>
public class PackageVersion
{
    private string _versionString = string.Empty;

    public long Id { get; set; }

    public long PackageId { get; set; }

    public Package Package { get; set; } = null!;

    /// <summary>
    /// The version in canonical form (<see cref="AppVersions.ToCanonicalString"/>): "1.3.0", never
    /// "1.3" or "1.3.0.0". Setting it also sets <see cref="VersionKey"/>.
    /// </summary>
    public required string VersionString
    {
        get => _versionString;
        set
        {
            _versionString = value;
            VersionKey = AppVersions.TryParse(value, out var parsed) ? AppVersions.ToSortKey(parsed) : string.Empty;
        }
    }

    /// <summary>
    /// <see cref="AppVersions.ToSortKey"/> of the version: fixed-width text that sorts like the
    /// version, so SQL can order and compare versions.
    /// </summary>
    public string VersionKey { get; set; } = string.Empty;

    /// <summary>
    /// Release channel: a free name following <see cref="ChannelNames"/> ("stable", "beta", "rc", …).
    /// A version belongs to exactly one channel.
    /// </summary>
    public string Channel { get; set; } = ChannelNames.Stable;

    /// <summary>
    /// Markdown changelog.
    /// </summary>
    public string Changelog { get; set; } = string.Empty;

    public DateTime ReleasedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Hide from updates but allow direct download.
    /// </summary>
    public bool IsDeprecated { get; set; }

    /// <summary>
    /// One build per OS/arch combination.
    /// </summary>
    public ICollection<VersionBuild> Builds { get; set; } = [];
}
