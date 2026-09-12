using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Ai
{
    /// <inheritdoc />
    public partial class AddAiSurfaceAndRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Branch",
                schema: "ai",
                table: "conversations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SiteId",
                schema: "ai",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Surface",
                schema: "ai",
                table: "conversations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "console");

            migrationBuilder.CreateTable(
                name: "runs",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Task = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    FromSeq = table.Column<int>(type: "integer", nullable: false),
                    ToSeq = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Complexity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ChangesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ValidationJson = table.Column<string>(type: "jsonb", nullable: true),
                    MetricsJson = table.Column<string>(type: "jsonb", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_runs_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "ai",
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_TenantId_OwnerUserId_Surface_SiteId_UpdatedAt",
                schema: "ai",
                table: "conversations",
                columns: new[] { "TenantId", "OwnerUserId", "Surface", "SiteId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_runs_ConversationId_StartedAt",
                schema: "ai",
                table: "runs",
                columns: new[] { "ConversationId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runs",
                schema: "ai");

            migrationBuilder.DropIndex(
                name: "IX_conversations_TenantId_OwnerUserId_Surface_SiteId_UpdatedAt",
                schema: "ai",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "Branch",
                schema: "ai",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "SiteId",
                schema: "ai",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "Surface",
                schema: "ai",
                table: "conversations");
        }
    }
}
