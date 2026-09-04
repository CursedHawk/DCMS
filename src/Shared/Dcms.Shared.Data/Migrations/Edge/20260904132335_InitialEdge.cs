using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Edge
{
    /// <inheritdoc />
    public partial class InitialEdge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "edge");

            migrationBuilder.CreateTable(
                name: "acme_accounts",
                schema: "edge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DirectoryUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ContactEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    EncryptedAccountKey = table.Column<string>(type: "text", nullable: false),
                    AccountUrl = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_acme_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "certificates",
                schema: "edge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    PemChain = table.Column<string>(type: "text", nullable: false),
                    EncryptedPrivateKey = table.Column<string>(type: "text", nullable: false),
                    NotBefore = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NotAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Issuer = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    RenewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_certificates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "routes",
                schema: "edge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RouteId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Hosts = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PathPattern = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ClusterId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_routes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_acme_accounts_DirectoryUrl",
                schema: "edge",
                table: "acme_accounts",
                column: "DirectoryUrl",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_certificates_Hostname",
                schema: "edge",
                table: "certificates",
                column: "Hostname",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_certificates_NotAfter",
                schema: "edge",
                table: "certificates",
                column: "NotAfter");

            migrationBuilder.CreateIndex(
                name: "IX_routes_RouteId",
                schema: "edge",
                table: "routes",
                column: "RouteId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "acme_accounts",
                schema: "edge");

            migrationBuilder.DropTable(
                name: "certificates",
                schema: "edge");

            migrationBuilder.DropTable(
                name: "routes",
                schema: "edge");
        }
    }
}
