using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Sites
{
    /// <inheritdoc />
    public partial class AddSiteDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "site_drafts",
                schema: "sites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Branch = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false),
                    BaseSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_site_drafts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_site_drafts_TenantId_SiteId_UserId_Branch",
                schema: "sites",
                table: "site_drafts",
                columns: new[] { "TenantId", "SiteId", "UserId", "Branch" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "site_drafts",
                schema: "sites");
        }
    }
}
