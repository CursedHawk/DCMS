using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Media
{
    /// <inheritdoc />
    public partial class AddMediaFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FolderId",
                schema: "media",
                table: "media_assets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "media_folders",
                schema: "media",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_folders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_TenantId_FolderId",
                schema: "media",
                table: "media_assets",
                columns: new[] { "TenantId", "FolderId" });

            migrationBuilder.CreateIndex(
                name: "IX_media_folders_TenantId_ParentId",
                schema: "media",
                table: "media_folders",
                columns: new[] { "TenantId", "ParentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_folders",
                schema: "media");

            migrationBuilder.DropIndex(
                name: "IX_media_assets_TenantId_FolderId",
                schema: "media",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "FolderId",
                schema: "media",
                table: "media_assets");
        }
    }
}
