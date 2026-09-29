using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Cms
{
    /// <inheritdoc />
    public partial class PluginDataSandbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_plugin_data_TenantId_PluginId_InstanceId_Collection_Key",
                schema: "plugins",
                table: "plugin_data");

            migrationBuilder.AddColumn<bool>(
                name: "IsSandbox",
                schema: "plugins",
                table: "plugin_data",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_plugin_data_TenantId_IsSandbox_PluginId_InstanceId_Collecti~",
                schema: "plugins",
                table: "plugin_data",
                columns: new[] { "TenantId", "IsSandbox", "PluginId", "InstanceId", "Collection", "Key" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_plugin_data_TenantId_IsSandbox_PluginId_InstanceId_Collecti~",
                schema: "plugins",
                table: "plugin_data");

            migrationBuilder.DropColumn(
                name: "IsSandbox",
                schema: "plugins",
                table: "plugin_data");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_data_TenantId_PluginId_InstanceId_Collection_Key",
                schema: "plugins",
                table: "plugin_data",
                columns: new[] { "TenantId", "PluginId", "InstanceId", "Collection", "Key" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
