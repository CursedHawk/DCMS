using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Sites
{
    /// <inheritdoc />
    public partial class SiteAuditBuilds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AuditExpiresAt",
                schema: "sites",
                table: "site_builds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuditToken",
                schema: "sites",
                table: "site_builds",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_site_builds_AuditToken",
                schema: "sites",
                table: "site_builds",
                column: "AuditToken",
                unique: true,
                filter: "\"AuditToken\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_site_builds_AuditToken",
                schema: "sites",
                table: "site_builds");

            migrationBuilder.DropColumn(
                name: "AuditExpiresAt",
                schema: "sites",
                table: "site_builds");

            migrationBuilder.DropColumn(
                name: "AuditToken",
                schema: "sites",
                table: "site_builds");
        }
    }
}
