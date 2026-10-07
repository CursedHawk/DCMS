using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Identity.Migrations
{
    /// <inheritdoc />
    public partial class TenantRealms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "realms",
                schema: "identity",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Hosts = table.Column<List<string>>(type: "text[]", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_realms", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "realm_groups",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_realm_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_realm_groups_realms_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "identity",
                        principalTable: "realms",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "realm_users",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSignInAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_realm_users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_realm_users_realms_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "identity",
                        principalTable: "realms",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "realm_group_members",
                schema: "identity",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_realm_group_members", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_realm_group_members_realm_groups_GroupId",
                        column: x => x.GroupId,
                        principalSchema: "identity",
                        principalTable: "realm_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_realm_group_members_realm_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "realm_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "realm_logins",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_realm_logins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_realm_logins_realm_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "realm_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_realm_group_members_TenantId_UserId",
                schema: "identity",
                table: "realm_group_members",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_realm_group_members_UserId",
                schema: "identity",
                table: "realm_group_members",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_realm_groups_TenantId_Name",
                schema: "identity",
                table: "realm_groups",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_realm_logins_TenantId_Provider_ProviderKey",
                schema: "identity",
                table: "realm_logins",
                columns: new[] { "TenantId", "Provider", "ProviderKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_realm_logins_UserId",
                schema: "identity",
                table: "realm_logins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_realm_users_TenantId_NormalizedEmail",
                schema: "identity",
                table: "realm_users",
                columns: new[] { "TenantId", "NormalizedEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_realms_Slug",
                schema: "identity",
                table: "realms",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "realm_group_members",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "realm_logins",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "realm_groups",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "realm_users",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "realms",
                schema: "identity");
        }
    }
}
