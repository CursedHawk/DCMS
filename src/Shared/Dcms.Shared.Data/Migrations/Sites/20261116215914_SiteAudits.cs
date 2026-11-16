using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Sites
{
    /// <inheritdoc />
    public partial class SiteAudits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "site_audits",
                schema: "sites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Target = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Branch = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    BuildId = table.Column<Guid>(type: "uuid", nullable: true),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MobileJson = table.Column<string>(type: "jsonb", nullable: true),
                    DesktopJson = table.Column<string>(type: "jsonb", nullable: true),
                    Error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_site_audits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_site_audits_SiteId_CreatedAt",
                schema: "sites",
                table: "site_audits",
                columns: new[] { "SiteId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "site_audits",
                schema: "sites");
        }
    }
}
