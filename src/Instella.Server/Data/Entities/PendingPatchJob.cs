namespace Instella.Server.Data.Entities;

public class PendingPatchJob
{
    public long Id { get; set; }
    public long? FromBuildId { get; set; }
    public long ToBuildId { get; set; }
    public PatchJobStatus Status { get; set; } = PatchJobStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// While <see cref="PatchJobStatus.InProgress"/>: when the worker's claim runs out. A job whose
    /// lease expired (the worker crashed) is claimable again.
    /// </summary>
    public DateTime? LeaseExpiresAt { get; set; }
    public uint RowVersion { get; set; }

    public VersionBuild? FromBuild { get; set; }
    public VersionBuild? ToBuild { get; set; }
}

public enum PatchJobStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Dead // max attempts exceeded
}
