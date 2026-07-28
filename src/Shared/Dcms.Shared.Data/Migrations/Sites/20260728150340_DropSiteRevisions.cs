using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Sites
{
    /// <inheritdoc />
    public partial class DropSiteRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "site_file_blobs",
                schema: "sites");

            migrationBuilder.DropTable(
                name: "site_revisions",
                schema: "sites");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "site_file_blobs",
                schema: "sites",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hash = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_site_file_blobs", x => new { x.TenantId, x.Hash });
                });

            migrationBuilder.CreateTable(
                name: "site_revisions",
                schema: "sites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Label = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ManifestJson = table.Column<string>(type: "jsonb", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_site_revisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_site_revisions_SiteId_CreatedAt",
                schema: "sites",
                table: "site_revisions",
                columns: new[] { "SiteId", "CreatedAt" });
        }
    }
}
