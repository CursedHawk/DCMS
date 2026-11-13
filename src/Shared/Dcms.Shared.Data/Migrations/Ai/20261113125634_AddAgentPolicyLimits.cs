using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Ai
{
    /// <inheritdoc />
    public partial class AddAgentPolicyLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AssistantMaxTurns",
                schema: "ai",
                table: "tenant_ai_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompactAtTokens",
                schema: "ai",
                table: "tenant_ai_settings",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssistantMaxTurns",
                schema: "ai",
                table: "tenant_ai_settings");

            migrationBuilder.DropColumn(
                name: "CompactAtTokens",
                schema: "ai",
                table: "tenant_ai_settings");
        }
    }
}
