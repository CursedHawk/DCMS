using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.UserAuth
{
    /// <inheritdoc />
    public partial class EnvironmentDimension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_roles_TenantId_Key",
                schema: "userauth",
                table: "roles");

            migrationBuilder.DropIndex(
                name: "IX_grants_TenantId_RoleId_SubjectType_SubjectId",
                schema: "userauth",
                table: "grants");

            migrationBuilder.DropIndex(
                name: "IX_gates_TenantId_SiteId_PathPrefix",
                schema: "userauth",
                table: "gates");

            migrationBuilder.DropIndex(
                name: "IX_api_rules_TenantId_InstanceId",
                schema: "userauth",
                table: "api_rules");

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "userauth",
                table: "roles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "userauth",
                table: "grants",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "userauth",
                table: "gates",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "userauth",
                table: "api_rules",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Every row so far is its tenant's production (ADR 0028), whose id is the tenant's. No
            // default afterwards: a row written without an environment must fail, not vanish.
            migrationBuilder.Sql("""
                UPDATE "userauth"."roles" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "userauth"."roles" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "userauth"."grants" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "userauth"."grants" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "userauth"."gates" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "userauth"."gates" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "userauth"."api_rules" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "userauth"."api_rules" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_roles_TenantId_EnvironmentId_Key",
                schema: "userauth",
                table: "roles",
                columns: new[] { "TenantId", "EnvironmentId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_grants_TenantId_EnvironmentId_RoleId_SubjectType_SubjectId",
                schema: "userauth",
                table: "grants",
                columns: new[] { "TenantId", "EnvironmentId", "RoleId", "SubjectType", "SubjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gates_TenantId_EnvironmentId_SiteId_PathPrefix",
                schema: "userauth",
                table: "gates",
                columns: new[] { "TenantId", "EnvironmentId", "SiteId", "PathPrefix" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_rules_TenantId_EnvironmentId_InstanceId",
                schema: "userauth",
                table: "api_rules",
                columns: new[] { "TenantId", "EnvironmentId", "InstanceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_roles_TenantId_EnvironmentId_Key",
                schema: "userauth",
                table: "roles");

            migrationBuilder.DropIndex(
                name: "IX_grants_TenantId_EnvironmentId_RoleId_SubjectType_SubjectId",
                schema: "userauth",
                table: "grants");

            migrationBuilder.DropIndex(
                name: "IX_gates_TenantId_EnvironmentId_SiteId_PathPrefix",
                schema: "userauth",
                table: "gates");

            migrationBuilder.DropIndex(
                name: "IX_api_rules_TenantId_EnvironmentId_InstanceId",
                schema: "userauth",
                table: "api_rules");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "userauth",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "userauth",
                table: "grants");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "userauth",
                table: "gates");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "userauth",
                table: "api_rules");

            migrationBuilder.CreateIndex(
                name: "IX_roles_TenantId_Key",
                schema: "userauth",
                table: "roles",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_grants_TenantId_RoleId_SubjectType_SubjectId",
                schema: "userauth",
                table: "grants",
                columns: new[] { "TenantId", "RoleId", "SubjectType", "SubjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gates_TenantId_SiteId_PathPrefix",
                schema: "userauth",
                table: "gates",
                columns: new[] { "TenantId", "SiteId", "PathPrefix" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_rules_TenantId_InstanceId",
                schema: "userauth",
                table: "api_rules",
                columns: new[] { "TenantId", "InstanceId" },
                unique: true);
        }
    }
}
