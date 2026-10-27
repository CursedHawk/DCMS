using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Visitors
{
    /// <inheritdoc />
    public partial class EnvironmentDimension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_visitor_accounts_TenantId_Email",
                schema: "visitors",
                table: "visitor_accounts");

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "visitors",
                table: "visitor_refresh_tokens",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "visitors",
                table: "visitor_accounts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Every row so far is its tenant's production (ADR 0028), whose id is the tenant's. No
            // default afterwards: a row written without an environment must fail, not vanish.
            migrationBuilder.Sql("""
                UPDATE "visitors"."visitor_refresh_tokens" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "visitors"."visitor_refresh_tokens" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "visitors"."visitor_accounts" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "visitors"."visitor_accounts" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_visitor_accounts_TenantId_EnvironmentId_Email",
                schema: "visitors",
                table: "visitor_accounts",
                columns: new[] { "TenantId", "EnvironmentId", "Email" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_visitor_accounts_TenantId_EnvironmentId_Email",
                schema: "visitors",
                table: "visitor_accounts");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "visitors",
                table: "visitor_refresh_tokens");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "visitors",
                table: "visitor_accounts");

            migrationBuilder.CreateIndex(
                name: "IX_visitor_accounts_TenantId_Email",
                schema: "visitors",
                table: "visitor_accounts",
                columns: new[] { "TenantId", "Email" },
                unique: true);
        }
    }
}
