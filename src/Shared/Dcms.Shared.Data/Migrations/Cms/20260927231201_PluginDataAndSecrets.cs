using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Cms
{
    /// <inheritdoc />
    public partial class PluginDataAndSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plugin_data",
                schema: "plugins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PluginId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Collection = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DataJson = table.Column<string>(type: "jsonb", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_data", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "plugin_secrets",
                schema: "plugins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PluginId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Ciphertext = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_secrets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plugin_data_DataJson",
                schema: "plugins",
                table: "plugin_data",
                column: "DataJson")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "jsonb_path_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_plugin_data_TenantId_PluginId_InstanceId_Collection_Key",
                schema: "plugins",
                table: "plugin_data",
                columns: new[] { "TenantId", "PluginId", "InstanceId", "Collection", "Key" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_plugin_secrets_TenantId_InstanceId_Name",
                schema: "plugins",
                table: "plugin_secrets",
                columns: new[] { "TenantId", "InstanceId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plugin_data",
                schema: "plugins");

            migrationBuilder.DropTable(
                name: "plugin_secrets",
                schema: "plugins");
        }
    }
}
