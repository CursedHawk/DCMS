using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Sites
{
    /// <inheritdoc />
    public partial class InitialSites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sites");

            migrationBuilder.CreateTable(
                name: "sites",
                schema: "sites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RenderMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DraftDefinitionJson = table.Column<string>(type: "jsonb", nullable: false),
                    ActiveBuildId = table.Column<Guid>(type: "uuid", nullable: true),
                    DefinitionVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "site_builds",
                schema: "sites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DefinitionSnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    ArtifactPrefix = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LogObjectKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_site_builds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_site_builds_sites_SiteId",
                        column: x => x.SiteId,
                        principalSchema: "sites",
                        principalTable: "sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_site_builds_SiteId_CreatedAt",
                schema: "sites",
                table: "site_builds",
                columns: new[] { "SiteId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "site_builds",
                schema: "sites");

            migrationBuilder.DropTable(
                name: "sites",
                schema: "sites");
        }
    }
}
