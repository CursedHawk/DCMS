using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Cms
{
    /// <inheritdoc />
    public partial class EnvironmentDimension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_plugin_secrets_TenantId_InstanceId_Name",
                schema: "plugins",
                table: "plugin_secrets");

            migrationBuilder.DropIndex(
                name: "IX_plugin_instances_TenantId_Slug",
                schema: "plugins",
                table: "plugin_instances");

            migrationBuilder.DropIndex(
                name: "IX_plugin_data_TenantId_PluginId_InstanceId_Collection_Key",
                schema: "plugins",
                table: "plugin_data");

            migrationBuilder.DropIndex(
                name: "IX_content_items_TenantId_PluginInstanceId_ContentType_Slug",
                schema: "cms",
                table: "content_items");

            migrationBuilder.DropIndex(
                name: "IX_api_connections_TenantId_Slug",
                schema: "cms",
                table: "api_connections");

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "cms",
                table: "scheduled_publishes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "plugins",
                table: "plugin_secrets",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "plugins",
                table: "plugin_instances",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "plugins",
                table: "plugin_data",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "cms",
                table: "content_versions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "cms",
                table: "content_outbox",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "cms",
                table: "content_items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "cms",
                table: "api_snapshots",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "cms",
                table: "api_connections",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Every row so far is its tenant's production (ADR 0028), whose id is the tenant's. No
            // default afterwards: a row written without an environment must fail, not vanish.
            migrationBuilder.Sql("""
                UPDATE "cms"."scheduled_publishes" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "cms"."scheduled_publishes" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "plugins"."plugin_secrets" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "plugins"."plugin_secrets" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "plugins"."plugin_instances" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "plugins"."plugin_instances" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "plugins"."plugin_data" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "plugins"."plugin_data" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "cms"."content_versions" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "cms"."content_versions" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "cms"."content_outbox" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "cms"."content_outbox" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "cms"."content_items" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "cms"."content_items" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "cms"."api_snapshots" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "cms"."api_snapshots" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "cms"."api_connections" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "cms"."api_connections" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_plugin_secrets_TenantId_EnvironmentId_InstanceId_Name",
                schema: "plugins",
                table: "plugin_secrets",
                columns: new[] { "TenantId", "EnvironmentId", "InstanceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plugin_instances_TenantId_EnvironmentId_Slug",
                schema: "plugins",
                table: "plugin_instances",
                columns: new[] { "TenantId", "EnvironmentId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plugin_data_TenantId_EnvironmentId_PluginId_InstanceId_Coll~",
                schema: "plugins",
                table: "plugin_data",
                columns: new[] { "TenantId", "EnvironmentId", "PluginId", "InstanceId", "Collection", "Key" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_content_items_TenantId_EnvironmentId_PluginInstanceId_Conte~",
                schema: "cms",
                table: "content_items",
                columns: new[] { "TenantId", "EnvironmentId", "PluginInstanceId", "ContentType", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_connections_TenantId_EnvironmentId_Slug",
                schema: "cms",
                table: "api_connections",
                columns: new[] { "TenantId", "EnvironmentId", "Slug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_plugin_secrets_TenantId_EnvironmentId_InstanceId_Name",
                schema: "plugins",
                table: "plugin_secrets");

            migrationBuilder.DropIndex(
                name: "IX_plugin_instances_TenantId_EnvironmentId_Slug",
                schema: "plugins",
                table: "plugin_instances");

            migrationBuilder.DropIndex(
                name: "IX_plugin_data_TenantId_EnvironmentId_PluginId_InstanceId_Coll~",
                schema: "plugins",
                table: "plugin_data");

            migrationBuilder.DropIndex(
                name: "IX_content_items_TenantId_EnvironmentId_PluginInstanceId_Conte~",
                schema: "cms",
                table: "content_items");

            migrationBuilder.DropIndex(
                name: "IX_api_connections_TenantId_EnvironmentId_Slug",
                schema: "cms",
                table: "api_connections");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "cms",
                table: "scheduled_publishes");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "plugins",
                table: "plugin_secrets");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "plugins",
                table: "plugin_instances");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "plugins",
                table: "plugin_data");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "cms",
                table: "content_versions");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "cms",
                table: "content_outbox");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "cms",
                table: "content_items");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "cms",
                table: "api_snapshots");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "cms",
                table: "api_connections");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_secrets_TenantId_InstanceId_Name",
                schema: "plugins",
                table: "plugin_secrets",
                columns: new[] { "TenantId", "InstanceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plugin_instances_TenantId_Slug",
                schema: "plugins",
                table: "plugin_instances",
                columns: new[] { "TenantId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plugin_data_TenantId_PluginId_InstanceId_Collection_Key",
                schema: "plugins",
                table: "plugin_data",
                columns: new[] { "TenantId", "PluginId", "InstanceId", "Collection", "Key" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_content_items_TenantId_PluginInstanceId_ContentType_Slug",
                schema: "cms",
                table: "content_items",
                columns: new[] { "TenantId", "PluginInstanceId", "ContentType", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_connections_TenantId_Slug",
                schema: "cms",
                table: "api_connections",
                columns: new[] { "TenantId", "Slug" },
                unique: true);
        }
    }
}
