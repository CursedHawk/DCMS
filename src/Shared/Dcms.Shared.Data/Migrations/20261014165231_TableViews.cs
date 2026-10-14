using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class TableViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "table_view_defaults",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TableKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ViewKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_table_view_defaults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "table_views",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TableKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Definition = table.Column<string>(type: "jsonb", nullable: false),
                    SharedWithWorkspace = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_table_views", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "table_view_shares",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ViewId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_table_view_shares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_table_view_shares_table_views_ViewId",
                        column: x => x.ViewId,
                        principalSchema: "tenancy",
                        principalTable: "table_views",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_table_view_defaults_TenantId_UserId_TableKey",
                schema: "tenancy",
                table: "table_view_defaults",
                columns: new[] { "TenantId", "UserId", "TableKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_table_view_shares_ViewId",
                schema: "tenancy",
                table: "table_view_shares",
                column: "ViewId");

            migrationBuilder.CreateIndex(
                name: "IX_table_views_TenantId_TableKey",
                schema: "tenancy",
                table: "table_views",
                columns: new[] { "TenantId", "TableKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "table_view_defaults",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "table_view_shares",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "table_views",
                schema: "tenancy");
        }
    }
}
