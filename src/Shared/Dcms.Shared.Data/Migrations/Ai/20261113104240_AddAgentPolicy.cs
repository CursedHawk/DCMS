using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Ai
{
    /// <inheritdoc />
    public partial class AddAgentPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AgentMaxTurns",
                schema: "ai",
                table: "tenant_ai_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromptOverridesJson",
                schema: "ai",
                table: "tenant_ai_settings",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentMaxTurns",
                schema: "ai",
                table: "tenant_ai_settings");

            migrationBuilder.DropColumn(
                name: "PromptOverridesJson",
                schema: "ai",
                table: "tenant_ai_settings");
        }
    }
}
