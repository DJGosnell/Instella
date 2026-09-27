using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Instella.Server.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminUsers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    TotpSecret = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    TotpEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastTotpTimeStep = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Packages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackageId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    IconPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DownloadAccessMode = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Packages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecurityEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventType = table.Column<int>(type: "INTEGER", nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 45, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ApiKeyName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PackageId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Details = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerSettings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StorageProvider = table.Column<int>(type: "INTEGER", nullable: false),
                    LocalBasePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    S3Preset = table.Column<int>(type: "INTEGER", nullable: false),
                    S3Endpoint = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    S3Bucket = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    S3AccessKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    S3SecretKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    S3Region = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    S3UrlExpiryMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    S3AllowInsecureEndpoint = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StoredFiles",
                columns: table => new
                {
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    StoragePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ReferenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PendingSince = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FirstUploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredFiles", x => x.ContentHash);
                });

            migrationBuilder.CreateTable(
                name: "UploadSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApiKeyId = table.Column<long>(type: "INTEGER", nullable: true),
                    PackageDbId = table.Column<long>(type: "INTEGER", nullable: false),
                    PackageId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OS = table.Column<int>(type: "INTEGER", nullable: false),
                    Architecture = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IpBans",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 45, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByAdminId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpBans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IpBans_AdminUsers_CreatedByAdminId",
                        column: x => x.CreatedByAdminId,
                        principalTable: "AdminUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    KeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false),
                    PackageId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsRevoked = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanUpload = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanDownload = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanManageVersions = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeys_Packages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DownloadTokens",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackageId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DisplayPrefix = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsRevoked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DownloadTokens_Packages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackagePublisherKeys",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackageId = table.Column<long>(type: "INTEGER", nullable: false),
                    KeyId = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackagePublisherKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackagePublisherKeys_Packages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageVersions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackageId = table.Column<long>(type: "INTEGER", nullable: false),
                    VersionString = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    VersionKey = table.Column<string>(type: "TEXT", maxLength: 43, nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "stable"),
                    Changelog = table.Column<string>(type: "TEXT", maxLength: 10000, nullable: false),
                    ReleasedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDeprecated = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackageVersions_Packages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UploadSessionFiles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Deduplicated = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadSessionFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UploadSessionFiles_UploadSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "UploadSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UploadSessionInstallers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Deduplicated = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadSessionInstallers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UploadSessionInstallers_UploadSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "UploadSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageChannels",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackageId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PinnedVersionId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageChannels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackageChannels_PackageVersions_PinnedVersionId",
                        column: x => x.PinnedVersionId,
                        principalTable: "PackageVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PackageChannels_Packages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VersionBuilds",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VersionId = table.Column<long>(type: "INTEGER", nullable: false),
                    OS = table.Column<int>(type: "INTEGER", nullable: false),
                    Architecture = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalSize = table.Column<long>(type: "INTEGER", nullable: false),
                    ManifestHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ReleaseManifestBytes = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ReleaseSignature = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ReleaseKeyId = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    IsDraft = table.Column<bool>(type: "INTEGER", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DownloadCount = table.Column<long>(type: "INTEGER", nullable: false),
                    PatchDownloadCount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VersionBuilds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VersionBuilds_PackageVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "PackageVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BuildFiles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BuildFiles_StoredFiles_ContentHash",
                        column: x => x.ContentHash,
                        principalTable: "StoredFiles",
                        principalColumn: "ContentHash",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BuildFiles_VersionBuilds_BuildId",
                        column: x => x.BuildId,
                        principalTable: "VersionBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BuildInstallers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    DownloadCount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildInstallers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BuildInstallers_StoredFiles_ContentHash",
                        column: x => x.ContentHash,
                        principalTable: "StoredFiles",
                        principalColumn: "ContentHash",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BuildInstallers_VersionBuilds_BuildId",
                        column: x => x.BuildId,
                        principalTable: "VersionBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BuildPatches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FromBuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    ToBuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    PatchSize = table.Column<long>(type: "INTEGER", nullable: false),
                    PatchHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StoragePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ManifestJson = table.Column<string>(type: "TEXT", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildPatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BuildPatches_VersionBuilds_FromBuildId",
                        column: x => x.FromBuildId,
                        principalTable: "VersionBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BuildPatches_VersionBuilds_ToBuildId",
                        column: x => x.ToBuildId,
                        principalTable: "VersionBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DownloadLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsPatch = table.Column<bool>(type: "INTEGER", nullable: false),
                    IPHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DownloadLogs_VersionBuilds_BuildId",
                        column: x => x.BuildId,
                        principalTable: "VersionBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingPatchJobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FromBuildId = table.Column<long>(type: "INTEGER", nullable: true),
                    ToBuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LeaseExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingPatchJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingPatchJobs_VersionBuilds_FromBuildId",
                        column: x => x.FromBuildId,
                        principalTable: "VersionBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PendingPatchJobs_VersionBuilds_ToBuildId",
                        column: x => x.ToBuildId,
                        principalTable: "VersionBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminUsers_Username",
                table: "AdminUsers",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_KeyHash",
                table: "ApiKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_PackageId",
                table: "ApiKeys",
                column: "PackageId");

            migrationBuilder.CreateIndex(
                name: "IX_BuildFiles_BuildId_RelativePath",
                table: "BuildFiles",
                columns: new[] { "BuildId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildFiles_ContentHash",
                table: "BuildFiles",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_BuildInstallers_BuildId_Kind",
                table: "BuildInstallers",
                columns: new[] { "BuildId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildInstallers_ContentHash",
                table: "BuildInstallers",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_BuildPatches_FromBuildId_ToBuildId",
                table: "BuildPatches",
                columns: new[] { "FromBuildId", "ToBuildId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildPatches_ToBuildId",
                table: "BuildPatches",
                column: "ToBuildId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadLogs_BuildId",
                table: "DownloadLogs",
                column: "BuildId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadLogs_Timestamp",
                table: "DownloadLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadTokens_PackageId",
                table: "DownloadTokens",
                column: "PackageId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadTokens_TokenHash",
                table: "DownloadTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IpBans_CreatedByAdminId",
                table: "IpBans",
                column: "CreatedByAdminId");

            migrationBuilder.CreateIndex(
                name: "IX_IpBans_IpAddress",
                table: "IpBans",
                column: "IpAddress",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackageChannels_PackageId_Name",
                table: "PackageChannels",
                columns: new[] { "PackageId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackageChannels_PinnedVersionId",
                table: "PackageChannels",
                column: "PinnedVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_PackagePublisherKeys_PackageId_KeyId",
                table: "PackagePublisherKeys",
                columns: new[] { "PackageId", "KeyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Packages_PackageId",
                table: "Packages",
                column: "PackageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackageVersions_PackageId_Channel_VersionKey",
                table: "PackageVersions",
                columns: new[] { "PackageId", "Channel", "VersionKey" });

            migrationBuilder.CreateIndex(
                name: "IX_PackageVersions_PackageId_VersionString",
                table: "PackageVersions",
                columns: new[] { "PackageId", "VersionString" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingPatchJobs_FromBuildId",
                table: "PendingPatchJobs",
                column: "FromBuildId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingPatchJobs_Status_NextAttemptAt",
                table: "PendingPatchJobs",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingPatchJobs_ToBuildId",
                table: "PendingPatchJobs",
                column: "ToBuildId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_EventType",
                table: "SecurityEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_IpAddress",
                table: "SecurityEvents",
                column: "IpAddress");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_Timestamp",
                table: "SecurityEvents",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_PendingSince",
                table: "StoredFiles",
                column: "PendingSince");

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessionFiles_ContentHash",
                table: "UploadSessionFiles",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessionFiles_SessionId_RelativePath",
                table: "UploadSessionFiles",
                columns: new[] { "SessionId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessionInstallers_ContentHash",
                table: "UploadSessionInstallers",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessionInstallers_SessionId_Kind",
                table: "UploadSessionInstallers",
                columns: new[] { "SessionId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_ExpiresAt",
                table: "UploadSessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_VersionBuilds_VersionId_OS_Architecture",
                table: "VersionBuilds",
                columns: new[] { "VersionId", "OS", "Architecture" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "BuildFiles");

            migrationBuilder.DropTable(
                name: "BuildInstallers");

            migrationBuilder.DropTable(
                name: "BuildPatches");

            migrationBuilder.DropTable(
                name: "DownloadLogs");

            migrationBuilder.DropTable(
                name: "DownloadTokens");

            migrationBuilder.DropTable(
                name: "IpBans");

            migrationBuilder.DropTable(
                name: "PackageChannels");

            migrationBuilder.DropTable(
                name: "PackagePublisherKeys");

            migrationBuilder.DropTable(
                name: "PendingPatchJobs");

            migrationBuilder.DropTable(
                name: "SecurityEvents");

            migrationBuilder.DropTable(
                name: "ServerSettings");

            migrationBuilder.DropTable(
                name: "UploadSessionFiles");

            migrationBuilder.DropTable(
                name: "UploadSessionInstallers");

            migrationBuilder.DropTable(
                name: "StoredFiles");

            migrationBuilder.DropTable(
                name: "AdminUsers");

            migrationBuilder.DropTable(
                name: "VersionBuilds");

            migrationBuilder.DropTable(
                name: "UploadSessions");

            migrationBuilder.DropTable(
                name: "PackageVersions");

            migrationBuilder.DropTable(
                name: "Packages");
        }
    }
}
