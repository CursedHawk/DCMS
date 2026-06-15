using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tenancy");

            migrationBuilder.CreateTable(
                name: "domains",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    VerificationToken = table.Column<string>(type: "text", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_domains", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "invitations",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RoleIdsCsv = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invitations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_memberships",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_memberships", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_roles",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Identifier = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "member_roles",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantRoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_member_roles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_member_roles_tenant_memberships_MembershipId",
                        column: x => x.MembershipId,
                        principalSchema: "tenancy",
                        principalTable: "tenant_memberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_member_roles_tenant_roles_TenantRoleId",
                        column: x => x.TenantRoleId,
                        principalSchema: "tenancy",
                        principalTable: "tenant_roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenant_role_permissions",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantRoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Permission = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_role_permissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tenant_role_permissions_tenant_roles_TenantRoleId",
                        column: x => x.TenantRoleId,
                        principalSchema: "tenancy",
                        principalTable: "tenant_roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_domains_Hostname",
                schema: "tenancy",
                table: "domains",
                column: "Hostname",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_domains_TenantId_IsPrimary",
                schema: "tenancy",
                table: "domains",
                columns: new[] { "TenantId", "IsPrimary" });

            migrationBuilder.CreateIndex(
                name: "IX_invitations_TenantId_Email",
                schema: "tenancy",
                table: "invitations",
                columns: new[] { "TenantId", "Email" });

            migrationBuilder.CreateIndex(
                name: "IX_invitations_TokenHash",
                schema: "tenancy",
                table: "invitations",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_member_roles_MembershipId_TenantRoleId",
                schema: "tenancy",
                table: "member_roles",
                columns: new[] { "MembershipId", "TenantRoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_member_roles_TenantRoleId",
                schema: "tenancy",
                table: "member_roles",
                column: "TenantRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_memberships_TenantId_UserId",
                schema: "tenancy",
                table: "tenant_memberships",
                columns: new[] { "TenantId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_role_permissions_TenantRoleId_Permission",
                schema: "tenancy",
                table: "tenant_role_permissions",
                columns: new[] { "TenantRoleId", "Permission" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_roles_TenantId_Name",
                schema: "tenancy",
                table: "tenant_roles",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenants_Identifier",
                schema: "tenancy",
                table: "tenants",
                column: "Identifier",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "domains",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "invitations",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "member_roles",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "tenant_role_permissions",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "tenants",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "tenant_memberships",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "tenant_roles",
                schema: "tenancy");
        }
    }
}
