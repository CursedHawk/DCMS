using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Cms
{
    /// <inheritdoc />
    public partial class PluginInstanceAiTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AiToolsEnabled",
                schema: "plugins",
                table: "plugin_instances",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiToolsEnabled",
                schema: "plugins",
                table: "plugin_instances");
        }
    }
}
