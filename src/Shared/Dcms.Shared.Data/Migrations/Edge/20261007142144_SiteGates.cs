using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Edge
{
    /// <inheritdoc />
    public partial class SiteGates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "site_gates",
                schema: "edge",
                columns: table => new
                {
                    Hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RealmSlug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    RulesJson = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_site_gates", x => x.Hostname);
                });

            migrationBuilder.CreateIndex(
                name: "IX_site_gates_TenantId",
                schema: "edge",
                table: "site_gates",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "site_gates",
                schema: "edge");
        }
    }
}
