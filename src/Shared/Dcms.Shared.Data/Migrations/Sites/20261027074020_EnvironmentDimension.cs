using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Sites
{
    /// <inheritdoc />
    public partial class EnvironmentDimension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_site_drafts_TenantId_SiteId_UserId_Branch",
                schema: "sites",
                table: "site_drafts");

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "sites",
                table: "sites",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "sites",
                table: "site_publish_branches",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "sites",
                table: "site_drafts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "sites",
                table: "site_builds",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Every row so far is its tenant's production (ADR 0028), whose id is the tenant's. No
            // default afterwards: a row written without an environment must fail, not vanish.
            migrationBuilder.Sql("""
                UPDATE "sites"."sites" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "sites"."sites" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "sites"."site_publish_branches" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "sites"."site_publish_branches" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "sites"."site_drafts" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "sites"."site_drafts" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "sites"."site_builds" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "sites"."site_builds" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_site_drafts_TenantId_EnvironmentId_SiteId_UserId_Branch",
                schema: "sites",
                table: "site_drafts",
                columns: new[] { "TenantId", "EnvironmentId", "SiteId", "UserId", "Branch" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_site_drafts_TenantId_EnvironmentId_SiteId_UserId_Branch",
                schema: "sites",
                table: "site_drafts");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "sites",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "sites",
                table: "site_publish_branches");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "sites",
                table: "site_drafts");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "sites",
                table: "site_builds");

            migrationBuilder.CreateIndex(
                name: "IX_site_drafts_TenantId_SiteId_UserId_Branch",
                schema: "sites",
                table: "site_drafts",
                columns: new[] { "TenantId", "SiteId", "UserId", "Branch" },
                unique: true);
        }
    }
}
