using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Sites
{
    /// <inheritdoc />
    public partial class SiteEnvironmentKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EnvironmentKey",
                schema: "sites",
                table: "sites",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_sites_TenantId_EnvironmentKey",
                schema: "sites",
                table: "sites",
                columns: new[] { "TenantId", "EnvironmentKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sites_TenantId_EnvironmentKey",
                schema: "sites",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "EnvironmentKey",
                schema: "sites",
                table: "sites");
        }
    }
}
