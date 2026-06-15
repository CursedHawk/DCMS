using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Cms
{
    /// <inheritdoc />
    public partial class InitialCms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "cms");

            migrationBuilder.EnsureSchema(
                name: "plugins");

            migrationBuilder.CreateTable(
                name: "content_items",
                schema: "cms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PluginInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Slug = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CurrentDraftVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PublishedVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "content_outbox",
                schema: "cms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "plugin_instances",
                schema: "plugins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PluginId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PluginVersion = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    ConfigJson = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_instances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "content_versions",
                schema: "cms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNo = table.Column<int>(type: "integer", nullable: false),
                    DataJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_content_versions_content_items_ItemId",
                        column: x => x.ItemId,
                        principalSchema: "cms",
                        principalTable: "content_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_content_items_TenantId_PluginInstanceId_ContentType_Slug",
                schema: "cms",
                table: "content_items",
                columns: new[] { "TenantId", "PluginInstanceId", "ContentType", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_content_outbox_SentAt",
                schema: "cms",
                table: "content_outbox",
                column: "SentAt");

            migrationBuilder.CreateIndex(
                name: "IX_content_versions_ItemId_VersionNo",
                schema: "cms",
                table: "content_versions",
                columns: new[] { "ItemId", "VersionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plugin_instances_TenantId_PluginId",
                schema: "plugins",
                table: "plugin_instances",
                columns: new[] { "TenantId", "PluginId" });

            migrationBuilder.CreateIndex(
                name: "IX_plugin_instances_TenantId_Slug",
                schema: "plugins",
                table: "plugin_instances",
                columns: new[] { "TenantId", "Slug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "content_outbox",
                schema: "cms");

            migrationBuilder.DropTable(
                name: "content_versions",
                schema: "cms");

            migrationBuilder.DropTable(
                name: "plugin_instances",
                schema: "plugins");

            migrationBuilder.DropTable(
                name: "content_items",
                schema: "cms");
        }
    }
}
