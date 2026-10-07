using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.UserAuth
{
    /// <inheritdoc />
    public partial class InitialUserAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "userauth");

            migrationBuilder.CreateTable(
                name: "gates",
                schema: "userauth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    PathPrefix = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Access = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Groups = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "userauth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Permissions = table.Column<List<string>>(type: "text[]", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "grants",
                schema: "userauth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_grants_roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "userauth",
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_gates_TenantId_SiteId_PathPrefix",
                schema: "userauth",
                table: "gates",
                columns: new[] { "TenantId", "SiteId", "PathPrefix" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gates_TenantId_SiteId_Position",
                schema: "userauth",
                table: "gates",
                columns: new[] { "TenantId", "SiteId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_grants_RoleId",
                schema: "userauth",
                table: "grants",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_grants_TenantId_RoleId_SubjectType_SubjectId",
                schema: "userauth",
                table: "grants",
                columns: new[] { "TenantId", "RoleId", "SubjectType", "SubjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_grants_TenantId_SubjectType_SubjectId",
                schema: "userauth",
                table: "grants",
                columns: new[] { "TenantId", "SubjectType", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_roles_TenantId_Key",
                schema: "userauth",
                table: "roles",
                columns: new[] { "TenantId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gates",
                schema: "userauth");

            migrationBuilder.DropTable(
                name: "grants",
                schema: "userauth");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "userauth");
        }
    }
}
