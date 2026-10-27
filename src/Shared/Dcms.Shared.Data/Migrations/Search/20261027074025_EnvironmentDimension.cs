using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Search
{
    /// <inheritdoc />
    public partial class EnvironmentDimension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_search_documents_TenantId_ContentItemId",
                schema: "search",
                table: "search_documents");

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "search",
                table: "search_documents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Every row so far is its tenant's production (ADR 0028), whose id is the tenant's. No
            // default afterwards: a row written without an environment must fail, not vanish.
            migrationBuilder.Sql("""
                UPDATE "search"."search_documents" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "search"."search_documents" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_search_documents_TenantId_EnvironmentId_ContentItemId",
                schema: "search",
                table: "search_documents",
                columns: new[] { "TenantId", "EnvironmentId", "ContentItemId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_search_documents_TenantId_EnvironmentId_ContentItemId",
                schema: "search",
                table: "search_documents");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "search",
                table: "search_documents");

            migrationBuilder.CreateIndex(
                name: "IX_search_documents_TenantId_ContentItemId",
                schema: "search",
                table: "search_documents",
                columns: new[] { "TenantId", "ContentItemId" },
                unique: true);
        }
    }
}
