using Instella.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Package> Packages => Set<Package>();
    public DbSet<PackageVersion> PackageVersions => Set<PackageVersion>();
    public DbSet<PackageChannel> PackageChannels => Set<PackageChannel>();
    public DbSet<DownloadToken> DownloadTokens => Set<DownloadToken>();
    public DbSet<VersionBuild> VersionBuilds => Set<VersionBuild>();
    public DbSet<BuildFile> BuildFiles => Set<BuildFile>();
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<BuildPatch> BuildPatches => Set<BuildPatch>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<DownloadLog> DownloadLogs => Set<DownloadLog>();
    public DbSet<ServerSettings> ServerSettings => Set<ServerSettings>();
    public DbSet<IpBan> IpBans => Set<IpBan>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();
    public DbSet<PendingPatchJob> PendingPatchJobs => Set<PendingPatchJob>();
    public DbSet<PackagePublisherKey> PackagePublisherKeys => Set<PackagePublisherKey>();
    public DbSet<UploadSessionRecord> UploadSessions => Set<UploadSessionRecord>();
    public DbSet<UploadSessionFile> UploadSessionFiles => Set<UploadSessionFile>();
    public DbSet<UploadSessionInstaller> UploadSessionInstallers => Set<UploadSessionInstaller>();
    public DbSet<BuildInstaller> BuildInstallers => Set<BuildInstaller>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Package
        modelBuilder.Entity<Package>(entity =>
        {
            entity.HasIndex(e => e.PackageId).IsUnique();
            entity.Property(e => e.PackageId).HasMaxLength(100);
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.IconPath).HasMaxLength(500);
            entity.Property(e => e.ReleaseDelayMinutes).HasDefaultValue(1440);
        });

        // PackageVersion
        modelBuilder.Entity<PackageVersion>(entity =>
        {
            entity.HasOne(e => e.Package)
                .WithMany(p => p.Versions)
                .HasForeignKey(e => e.PackageId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.PackageId, e.VersionString }).IsUnique();
            // "Latest" is the highest VersionKey on a channel.
            entity.HasIndex(e => new { e.PackageId, e.Channel, e.VersionKey });
            entity.Property(e => e.VersionString).HasMaxLength(50);
            entity.Property(e => e.VersionKey).HasMaxLength(43).IsRequired();
            entity.Property(e => e.Channel).HasMaxLength(Instella.Core.Wire.ChannelNames.MaxLength)
                .HasDefaultValue(Instella.Core.Wire.ChannelNames.Stable);
            entity.Property(e => e.Changelog).HasMaxLength(10000);
        });

        // DownloadToken
        modelBuilder.Entity<DownloadToken>(entity =>
        {
            entity.HasOne(e => e.Package)
                .WithMany()
                .HasForeignKey(e => e.PackageId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.Property(e => e.TokenHash).HasMaxLength(64);
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.DisplayPrefix).HasMaxLength(8);
        });

        // PackageChannel
        modelBuilder.Entity<PackageChannel>(entity =>
        {
            entity.HasOne(e => e.Package)
                .WithMany(p => p.Channels)
                .HasForeignKey(e => e.PackageId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.PinnedVersion)
                .WithMany()
                .HasForeignKey(e => e.PinnedVersionId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => new { e.PackageId, e.Name }).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(Instella.Core.Wire.ChannelNames.MaxLength);
        });

        // VersionBuild
        modelBuilder.Entity<VersionBuild>(entity =>
        {
            entity.HasOne(e => e.Version)
                .WithMany(v => v.Builds)
                .HasForeignKey(e => e.VersionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.VersionId, e.OS, e.Architecture }).IsUnique();
            entity.Property(e => e.ManifestHash).HasMaxLength(64);
            entity.Property(e => e.ReleaseSignature).HasMaxLength(200);
            entity.Property(e => e.ReleaseKeyId).HasMaxLength(16);
            entity.Property(e => e.UploadedByKeyName).HasMaxLength(200);
            // The delayed-release worker looks for pending builds whose hold has ended.
            entity.HasIndex(e => new { e.State, e.PublishAfter });
        });

        // PackagePublisherKey
        modelBuilder.Entity<PackagePublisherKey>(entity =>
        {
            entity.HasOne(e => e.Package)
                .WithMany(p => p.PublisherKeys)
                .HasForeignKey(e => e.PackageId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.PackageId, e.KeyId }).IsUnique();
            entity.Property(e => e.KeyId).HasMaxLength(16);
            entity.Property(e => e.PublicKey).HasMaxLength(500);
            entity.Property(e => e.Label).HasMaxLength(200);
        });

        // BuildFile
        modelBuilder.Entity<BuildFile>(entity =>
        {
            entity.HasOne(e => e.Build)
                .WithMany(b => b.Files)
                .HasForeignKey(e => e.BuildId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.StoredFile)
                .WithMany()
                .HasForeignKey(e => e.ContentHash)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.BuildId, e.RelativePath }).IsUnique();
            entity.Property(e => e.RelativePath).HasMaxLength(500);
            entity.Property(e => e.ContentHash).HasMaxLength(64);
        });

        // BuildInstaller
        modelBuilder.Entity<BuildInstaller>(entity =>
        {
            entity.HasOne(e => e.Build)
                .WithMany(b => b.Installers)
                .HasForeignKey(e => e.BuildId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.StoredFile)
                .WithMany()
                .HasForeignKey(e => e.ContentHash)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.BuildId, e.Kind }).IsUnique();
            entity.Property(e => e.Kind).HasMaxLength(16);
            entity.Property(e => e.FileName).HasMaxLength(128);
            entity.Property(e => e.ContentHash).HasMaxLength(64);
        });

        // StoredFile
        modelBuilder.Entity<StoredFile>(entity =>
        {
            entity.HasKey(e => e.ContentHash);
            entity.Property(e => e.ContentHash).HasMaxLength(64);
            entity.Property(e => e.StoragePath).HasMaxLength(500);
            entity.HasIndex(e => e.PendingSince);
        });

        // Upload sessions (durable)
        modelBuilder.Entity<UploadSessionRecord>(entity =>
        {
            entity.ToTable("UploadSessions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PackageId).HasMaxLength(100);
            entity.Property(e => e.Version).HasMaxLength(50);
            entity.Property(e => e.Channel).HasMaxLength(Instella.Core.Wire.ChannelNames.MaxLength);
            entity.HasIndex(e => e.ExpiresAt);
        });

        modelBuilder.Entity<UploadSessionFile>(entity =>
        {
            entity.HasOne(e => e.Session)
                .WithMany(s => s.Files)
                .HasForeignKey(e => e.SessionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.SessionId, e.RelativePath }).IsUnique();
            entity.HasIndex(e => e.ContentHash);
            entity.Property(e => e.RelativePath).HasMaxLength(500);
            entity.Property(e => e.ContentHash).HasMaxLength(64);
        });

        modelBuilder.Entity<UploadSessionInstaller>(entity =>
        {
            entity.HasOne(e => e.Session)
                .WithMany(s => s.Installers)
                .HasForeignKey(e => e.SessionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.SessionId, e.Kind }).IsUnique();
            entity.HasIndex(e => e.ContentHash);
            entity.Property(e => e.Kind).HasMaxLength(16);
            entity.Property(e => e.FileName).HasMaxLength(128);
            entity.Property(e => e.ContentHash).HasMaxLength(64);
        });

        // BuildPatch
        modelBuilder.Entity<BuildPatch>(entity =>
        {
            entity.HasOne(e => e.FromBuild)
                .WithMany(b => b.PatchesFrom)
                .HasForeignKey(e => e.FromBuildId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.ToBuild)
                .WithMany(b => b.PatchesTo)
                .HasForeignKey(e => e.ToBuildId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.FromBuildId, e.ToBuildId }).IsUnique();
            entity.Property(e => e.PatchHash).HasMaxLength(64);
            entity.Property(e => e.StoragePath).HasMaxLength(500);
        });

        // ApiKey
        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.HasOne(e => e.Package)
                .WithMany(p => p.ApiKeys)
                .HasForeignKey(e => e.PackageId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.KeyHash).IsUnique();
            entity.Property(e => e.KeyHash).HasMaxLength(64);
            entity.Property(e => e.Name).HasMaxLength(200);
        });

        // AdminUser
        modelBuilder.Entity<AdminUser>(entity =>
        {
            entity.HasIndex(e => e.Username).IsUnique();
            entity.Property(e => e.Username).HasMaxLength(100);
            entity.Property(e => e.PasswordHash).HasMaxLength(500);
            entity.Property(e => e.TotpSecret).HasMaxLength(500);
        });

        // DownloadLog
        modelBuilder.Entity<DownloadLog>(entity =>
        {
            entity.HasOne(e => e.Build)
                .WithMany(b => b.DownloadLogs)
                .HasForeignKey(e => e.BuildId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.Timestamp);
            entity.Property(e => e.IPHash).HasMaxLength(64);
            entity.Property(e => e.UserAgent).HasMaxLength(500);
        });

        // ServerSettings
        modelBuilder.Entity<ServerSettings>(entity =>
        {
            entity.Property(e => e.LocalBasePath).HasMaxLength(500);
            entity.Property(e => e.S3Endpoint).HasMaxLength(500);
            entity.Property(e => e.S3Bucket).HasMaxLength(200);
            entity.Property(e => e.S3AccessKey).HasMaxLength(200);
            entity.Property(e => e.S3SecretKey).HasMaxLength(500);
            entity.Property(e => e.S3Region).HasMaxLength(50);
        });

        // IpBan
        modelBuilder.Entity<IpBan>(entity =>
        {
            entity.HasIndex(e => e.IpAddress).IsUnique();
            entity.Property(e => e.IpAddress).HasMaxLength(45); // IPv6 max length
            entity.Property(e => e.Reason).HasMaxLength(500);
            entity.HasOne(e => e.CreatedByAdmin)
                .WithMany()
                .HasForeignKey(e => e.CreatedByAdminId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // PendingPatchJob
        modelBuilder.Entity<PendingPatchJob>(entity =>
        {
            entity.HasOne(e => e.FromBuild)
                .WithMany()
                .HasForeignKey(e => e.FromBuildId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.ToBuild)
                .WithMany()
                .HasForeignKey(e => e.ToBuildId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.Status, e.NextAttemptAt });
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.RowVersion).IsConcurrencyToken();
        });

        // SecurityEvent
        modelBuilder.Entity<SecurityEvent>(entity =>
        {
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.EventType);
            entity.HasIndex(e => e.IpAddress);
            entity.Property(e => e.IpAddress).HasMaxLength(45);
            entity.Property(e => e.Username).HasMaxLength(100);
            entity.Property(e => e.ApiKeyName).HasMaxLength(100);
            entity.Property(e => e.PackageId).HasMaxLength(100);
            entity.Property(e => e.Details).HasMaxLength(1000);
        });
    }
}
