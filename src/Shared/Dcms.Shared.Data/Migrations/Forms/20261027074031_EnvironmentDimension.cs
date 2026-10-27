using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Forms
{
    /// <inheritdoc />
    public partial class EnvironmentDimension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "forms",
                table: "form_submissions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Every row so far is its tenant's production (ADR 0028), whose id is the tenant's. No
            // default afterwards: a row written without an environment must fail, not vanish.
            migrationBuilder.Sql("""
                UPDATE "forms"."form_submissions" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "forms"."form_submissions" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "forms",
                table: "form_submissions");
        }
    }
}
