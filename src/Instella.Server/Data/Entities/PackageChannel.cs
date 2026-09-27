namespace Instella.Server.Data.Entities;

/// <summary>
/// A channel of a package ("stable", "beta", …). Created by the first upload to it; the
/// download page and the admin UI list these. An optional pin caps what "latest" means on the
/// channel, which holds back a staged rollout without deleting anything.
/// </summary>
public class PackageChannel
{
    public long Id { get; set; }

    public long PackageId { get; set; }

    public Package Package { get; set; } = null!;

    /// <summary>The channel name (<see cref="Instella.Core.Wire.ChannelNames"/>).</summary>
    public required string Name { get; set; }

    /// <summary>The highest version "latest" may be on this channel; null when uncapped.</summary>
    public long? PinnedVersionId { get; set; }

    public PackageVersion? PinnedVersion { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
