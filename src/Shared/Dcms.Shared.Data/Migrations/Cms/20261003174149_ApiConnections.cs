using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Cms
{
    /// <inheritdoc />
    public partial class ApiConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_connections",
                schema: "cms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    AuthKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AuthName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SecretCiphertext = table.Column<string>(type: "text", nullable: true),
                    Operations = table.Column<List<string>>(type: "text[]", nullable: false),
                    RefreshMinutes = table.Column<int>(type: "integer", nullable: false),
                    RefreshedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "api_snapshots",
                schema: "cms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Operation = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Body = table.Column<string>(type: "jsonb", nullable: false),
                    ItemsPath = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Fields = table.Column<List<string>>(type: "text[]", nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_api_snapshots_api_connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalSchema: "cms",
                        principalTable: "api_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_connections_TenantId_Slug",
                schema: "cms",
                table: "api_connections",
                columns: new[] { "TenantId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_snapshots_ConnectionId_Operation",
                schema: "cms",
                table: "api_snapshots",
                columns: new[] { "ConnectionId", "Operation" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_snapshots",
                schema: "cms");

            migrationBuilder.DropTable(
                name: "api_connections",
                schema: "cms");
        }
    }
}
