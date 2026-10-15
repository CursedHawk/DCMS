using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Jobs
{
    /// <inheritdoc />
    public partial class InitialJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "jobs");

            migrationBuilder.CreateTable(
                name: "jobs",
                schema: "jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Lane = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TitleKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ParamsJson = table.Column<string>(type: "jsonb", nullable: false),
                    RequiredPermission = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LinkPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Done = table.Column<long>(type: "bigint", nullable: false),
                    Total = table.Column<long>(type: "bigint", nullable: true),
                    StageKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ErrorKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ErrorDetail = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: true),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                    ContextJson = table.Column<string>(type: "jsonb", nullable: true),
                    Cancelable = table.Column<bool>(type: "boolean", nullable: false),
                    CancelRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    DedupeKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_ParentId",
                schema: "jobs",
                table: "jobs",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_Status_UpdatedAt",
                schema: "jobs",
                table: "jobs",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_TenantId_DedupeKey",
                schema: "jobs",
                table: "jobs",
                columns: new[] { "TenantId", "DedupeKey" },
                unique: true,
                filter: "\"DedupeKey\" IS NOT NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_TenantId_Status_CreatedAt",
                schema: "jobs",
                table: "jobs",
                columns: new[] { "TenantId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "jobs",
                schema: "jobs");
        }
    }
}
