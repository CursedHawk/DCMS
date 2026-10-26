using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnvironmentLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "environment_links",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TargetKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Access = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environment_links", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_environment_links_TargetTenantId",
                schema: "tenancy",
                table: "environment_links",
                column: "TargetTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_environment_links_TenantId_Kind_TargetKey",
                schema: "tenancy",
                table: "environment_links",
                columns: new[] { "TenantId", "Kind", "TargetKey" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "environment_links",
                schema: "tenancy");
        }
    }
}
