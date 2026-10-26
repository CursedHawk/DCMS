using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Visitors
{
    /// <inheritdoc />
    public partial class DropPreviewSandbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The preview sandbox is gone (ADR 0026): its rows are test data nobody can reach any
            // more, and a visitor's sandbox account would collide with their live one on the new
            // unique (TenantId, Email) index.
            migrationBuilder.Sql("""DELETE FROM visitors.visitor_refresh_tokens WHERE "IsSandbox";""");
            migrationBuilder.Sql("""DELETE FROM visitors.visitor_accounts WHERE "IsSandbox";""");

            migrationBuilder.DropIndex(
                name: "IX_visitor_accounts_TenantId_IsSandbox_Email",
                schema: "visitors",
                table: "visitor_accounts");

            migrationBuilder.DropColumn(
                name: "IsSandbox",
                schema: "visitors",
                table: "visitor_refresh_tokens");

            migrationBuilder.DropColumn(
                name: "IsSandbox",
                schema: "visitors",
                table: "visitor_accounts");

            migrationBuilder.CreateIndex(
                name: "IX_visitor_accounts_TenantId_Email",
                schema: "visitors",
                table: "visitor_accounts",
                columns: new[] { "TenantId", "Email" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_visitor_accounts_TenantId_Email",
                schema: "visitors",
                table: "visitor_accounts");

            migrationBuilder.AddColumn<bool>(
                name: "IsSandbox",
                schema: "visitors",
                table: "visitor_refresh_tokens",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSandbox",
                schema: "visitors",
                table: "visitor_accounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_visitor_accounts_TenantId_IsSandbox_Email",
                schema: "visitors",
                table: "visitor_accounts",
                columns: new[] { "TenantId", "IsSandbox", "Email" },
                unique: true);
        }
    }
}
