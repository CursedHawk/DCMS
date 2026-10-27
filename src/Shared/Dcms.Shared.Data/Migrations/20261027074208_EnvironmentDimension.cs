using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnvironmentDimension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tenant_roles_TenantId_Name",
                schema: "tenancy",
                table: "tenant_roles");

            migrationBuilder.DropIndex(
                name: "IX_table_view_defaults_TenantId_UserId_TableKey",
                schema: "tenancy",
                table: "table_view_defaults");

            migrationBuilder.DropIndex(
                name: "IX_environment_policies_TenantId_Kind_ItemKey",
                schema: "tenancy",
                table: "environment_policies");

            migrationBuilder.DropIndex(
                name: "IX_environment_migrations_TenantId_Order",
                schema: "tenancy",
                table: "environment_migrations");

            migrationBuilder.DropIndex(
                name: "IX_environment_links_TenantId_Kind_TargetKey",
                schema: "tenancy",
                table: "environment_links");

            migrationBuilder.DropIndex(
                name: "IX_environment_baselines_TenantId",
                schema: "tenancy",
                table: "environment_baselines");

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "tenant_roles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "tenant_role_permissions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "table_views",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "table_view_shares",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "table_view_defaults",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "member_roles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "environment_policies",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "environment_migrations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "environment_links",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "environment_baselines",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "domains",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Every row so far is its tenant's production (ADR 0028), whose id is the tenant's. No
            // default afterwards: a row written without an environment must fail, not vanish.
            migrationBuilder.Sql("""
                UPDATE "tenancy"."tenant_roles" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "tenancy"."tenant_roles" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "tenancy"."tenant_role_permissions" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "tenancy"."tenant_role_permissions" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "tenancy"."table_views" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "tenancy"."table_views" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "tenancy"."table_view_shares" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "tenancy"."table_view_shares" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "tenancy"."table_view_defaults" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "tenancy"."table_view_defaults" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "tenancy"."member_roles" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "tenancy"."member_roles" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "tenancy"."environment_policies" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "tenancy"."environment_policies" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "tenancy"."environment_migrations" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "tenancy"."environment_migrations" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "tenancy"."environment_links" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "tenancy"."environment_links" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "tenancy"."environment_baselines" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "tenancy"."environment_baselines" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "tenancy"."domains" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "tenancy"."domains" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_roles_TenantId_EnvironmentId_Name",
                schema: "tenancy",
                table: "tenant_roles",
                columns: new[] { "TenantId", "EnvironmentId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_table_view_defaults_TenantId_EnvironmentId_UserId_TableKey",
                schema: "tenancy",
                table: "table_view_defaults",
                columns: new[] { "TenantId", "EnvironmentId", "UserId", "TableKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_environment_policies_TenantId_EnvironmentId_Kind_ItemKey",
                schema: "tenancy",
                table: "environment_policies",
                columns: new[] { "TenantId", "EnvironmentId", "Kind", "ItemKey" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_environment_migrations_TenantId_EnvironmentId_Order",
                schema: "tenancy",
                table: "environment_migrations",
                columns: new[] { "TenantId", "EnvironmentId", "Order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_environment_links_TenantId_EnvironmentId_Kind_TargetKey",
                schema: "tenancy",
                table: "environment_links",
                columns: new[] { "TenantId", "EnvironmentId", "Kind", "TargetKey" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_environment_baselines_TenantId_EnvironmentId",
                schema: "tenancy",
                table: "environment_baselines",
                columns: new[] { "TenantId", "EnvironmentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tenant_roles_TenantId_EnvironmentId_Name",
                schema: "tenancy",
                table: "tenant_roles");

            migrationBuilder.DropIndex(
                name: "IX_table_view_defaults_TenantId_EnvironmentId_UserId_TableKey",
                schema: "tenancy",
                table: "table_view_defaults");

            migrationBuilder.DropIndex(
                name: "IX_environment_policies_TenantId_EnvironmentId_Kind_ItemKey",
                schema: "tenancy",
                table: "environment_policies");

            migrationBuilder.DropIndex(
                name: "IX_environment_migrations_TenantId_EnvironmentId_Order",
                schema: "tenancy",
                table: "environment_migrations");

            migrationBuilder.DropIndex(
                name: "IX_environment_links_TenantId_EnvironmentId_Kind_TargetKey",
                schema: "tenancy",
                table: "environment_links");

            migrationBuilder.DropIndex(
                name: "IX_environment_baselines_TenantId_EnvironmentId",
                schema: "tenancy",
                table: "environment_baselines");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "tenant_roles");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "tenant_role_permissions");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "table_views");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "table_view_shares");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "table_view_defaults");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "member_roles");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "environment_policies");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "environment_migrations");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "environment_links");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "environment_baselines");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "tenancy",
                table: "domains");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_roles_TenantId_Name",
                schema: "tenancy",
                table: "tenant_roles",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_table_view_defaults_TenantId_UserId_TableKey",
                schema: "tenancy",
                table: "table_view_defaults",
                columns: new[] { "TenantId", "UserId", "TableKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_environment_policies_TenantId_Kind_ItemKey",
                schema: "tenancy",
                table: "environment_policies",
                columns: new[] { "TenantId", "Kind", "ItemKey" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_environment_migrations_TenantId_Order",
                schema: "tenancy",
                table: "environment_migrations",
                columns: new[] { "TenantId", "Order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_environment_links_TenantId_Kind_TargetKey",
                schema: "tenancy",
                table: "environment_links",
                columns: new[] { "TenantId", "Kind", "TargetKey" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_environment_baselines_TenantId",
                schema: "tenancy",
                table: "environment_baselines",
                column: "TenantId",
                unique: true);
        }
    }
}
