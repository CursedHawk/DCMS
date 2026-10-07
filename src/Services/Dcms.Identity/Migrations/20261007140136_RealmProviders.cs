using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Identity.Migrations
{
    /// <inheritdoc />
    public partial class RealmProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PasswordEnabled",
                schema: "identity",
                table: "realms",
                type: "boolean",
                nullable: false,
                // Realms that exist already keep the password sign-in they had.
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "realm_providers",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    EntraTenant = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    HostedDomain = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SecretCiphertext = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    Provisioning = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    AllowedDomains = table.Column<List<string>>(type: "text[]", nullable: false),
                    DefaultGroups = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    GroupClaim = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    GroupMappings = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_realm_providers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_realm_providers_realms_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "identity",
                        principalTable: "realms",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_realm_providers_TenantId_Key",
                schema: "identity",
                table: "realm_providers",
                columns: new[] { "TenantId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "realm_providers",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "PasswordEnabled",
                schema: "identity",
                table: "realms");
        }
    }
}
