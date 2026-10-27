using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Social
{
    /// <inheritdoc />
    public partial class EnvironmentDimension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_meta_sync_states_TenantId_PluginInstanceId_ContentType",
                schema: "social",
                table: "meta_sync_states");

            migrationBuilder.DropIndex(
                name: "IX_meta_media_map_TenantId_ConnectionId_ExternalMediaId",
                schema: "social",
                table: "meta_media_map");

            migrationBuilder.DropIndex(
                name: "IX_meta_connections_TenantId_Provider_ExternalAccountId",
                schema: "social",
                table: "meta_connections");

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "social",
                table: "meta_sync_states",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "social",
                table: "meta_oauth_states",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "social",
                table: "meta_media_map",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "social",
                table: "meta_connections",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Every row so far is its tenant's production (ADR 0028), whose id is the tenant's. No
            // default afterwards: a row written without an environment must fail, not vanish.
            migrationBuilder.Sql("""
                UPDATE "social"."meta_sync_states" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "social"."meta_sync_states" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "social"."meta_oauth_states" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "social"."meta_oauth_states" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "social"."meta_media_map" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "social"."meta_media_map" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "social"."meta_connections" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "social"."meta_connections" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_meta_sync_states_TenantId_EnvironmentId_PluginInstanceId_Co~",
                schema: "social",
                table: "meta_sync_states",
                columns: new[] { "TenantId", "EnvironmentId", "PluginInstanceId", "ContentType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_meta_media_map_TenantId_EnvironmentId_ConnectionId_External~",
                schema: "social",
                table: "meta_media_map",
                columns: new[] { "TenantId", "EnvironmentId", "ConnectionId", "ExternalMediaId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_meta_connections_TenantId_EnvironmentId_Provider_ExternalAc~",
                schema: "social",
                table: "meta_connections",
                columns: new[] { "TenantId", "EnvironmentId", "Provider", "ExternalAccountId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_meta_sync_states_TenantId_EnvironmentId_PluginInstanceId_Co~",
                schema: "social",
                table: "meta_sync_states");

            migrationBuilder.DropIndex(
                name: "IX_meta_media_map_TenantId_EnvironmentId_ConnectionId_External~",
                schema: "social",
                table: "meta_media_map");

            migrationBuilder.DropIndex(
                name: "IX_meta_connections_TenantId_EnvironmentId_Provider_ExternalAc~",
                schema: "social",
                table: "meta_connections");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "social",
                table: "meta_sync_states");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "social",
                table: "meta_oauth_states");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "social",
                table: "meta_media_map");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "social",
                table: "meta_connections");

            migrationBuilder.CreateIndex(
                name: "IX_meta_sync_states_TenantId_PluginInstanceId_ContentType",
                schema: "social",
                table: "meta_sync_states",
                columns: new[] { "TenantId", "PluginInstanceId", "ContentType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_meta_media_map_TenantId_ConnectionId_ExternalMediaId",
                schema: "social",
                table: "meta_media_map",
                columns: new[] { "TenantId", "ConnectionId", "ExternalMediaId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_meta_connections_TenantId_Provider_ExternalAccountId",
                schema: "social",
                table: "meta_connections",
                columns: new[] { "TenantId", "Provider", "ExternalAccountId" },
                unique: true);
        }
    }
}
