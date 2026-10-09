using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Identity.Migrations
{
    /// <inheritdoc />
    public partial class BrowserAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "browser_accounts",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoginSessionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SecurityStamp = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_browser_accounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_browser_accounts_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_browser_accounts_DeviceHash_UserId",
                schema: "identity",
                table: "browser_accounts",
                columns: new[] { "DeviceHash", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_browser_accounts_LoginSessionId",
                schema: "identity",
                table: "browser_accounts",
                column: "LoginSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_browser_accounts_UserId",
                schema: "identity",
                table: "browser_accounts",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "browser_accounts",
                schema: "identity");
        }
    }
}
