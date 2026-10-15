using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.DynamicApps
{
    /// <inheritdoc />
    public partial class AppSiteViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "site_view_defaults",
                schema: "apps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TableId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ViewKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_site_view_defaults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "site_views",
                schema: "apps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TableId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false),
                    SharedWithEveryone = table.Column<bool>(type: "boolean", nullable: false),
                    SharedGroups = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    SharedUsers = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_site_views", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_site_view_defaults_TenantId_InstanceId_TableId_UserId",
                schema: "apps",
                table: "site_view_defaults",
                columns: new[] { "TenantId", "InstanceId", "TableId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_site_views_TenantId_InstanceId_TableId_OwnerUserId",
                schema: "apps",
                table: "site_views",
                columns: new[] { "TenantId", "InstanceId", "TableId", "OwnerUserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "site_view_defaults",
                schema: "apps");

            migrationBuilder.DropTable(
                name: "site_views",
                schema: "apps");
        }
    }
}
