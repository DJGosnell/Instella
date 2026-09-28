using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Instella.Server.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IsDraft (0/1) becomes State: 0 is Published and 1 is Draft, so every row keeps its meaning.
            migrationBuilder.RenameColumn(
                name: "IsDraft",
                table: "VersionBuilds",
                newName: "State");

            migrationBuilder.AddColumn<DateTime>(
                name: "PublishAfter",
                table: "VersionBuilds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "UploadedByApiKeyId",
                table: "VersionBuilds",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UploadedByKeyName",
                table: "VersionBuilds",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReleaseApproval",
                table: "Packages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReleaseDelayMinutes",
                table: "Packages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1440);

            migrationBuilder.AddColumn<bool>(
                name: "CanApproveReleases",
                table: "ApiKeys",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_VersionBuilds_State_PublishAfter",
                table: "VersionBuilds",
                columns: new[] { "State", "PublishAfter" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VersionBuilds_State_PublishAfter",
                table: "VersionBuilds");

            migrationBuilder.DropColumn(
                name: "PublishAfter",
                table: "VersionBuilds");

            migrationBuilder.DropColumn(
                name: "UploadedByApiKeyId",
                table: "VersionBuilds");

            migrationBuilder.DropColumn(
                name: "UploadedByKeyName",
                table: "VersionBuilds");

            migrationBuilder.DropColumn(
                name: "ReleaseApproval",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "ReleaseDelayMinutes",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "CanApproveReleases",
                table: "ApiKeys");

            // A pending build has no IsDraft equivalent; it stays hidden as a draft rather than going live.
            migrationBuilder.Sql("UPDATE VersionBuilds SET State = 1 WHERE State = 2");

            migrationBuilder.RenameColumn(
                name: "State",
                table: "VersionBuilds",
                newName: "IsDraft");
        }
    }
}
