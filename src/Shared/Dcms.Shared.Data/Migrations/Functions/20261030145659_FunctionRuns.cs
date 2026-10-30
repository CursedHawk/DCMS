using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Functions
{
    /// <inheritdoc />
    public partial class FunctionRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "runs",
                schema: "functions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Function = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeploymentNumber = table.Column<int>(type: "integer", nullable: false),
                    TriggerKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TriggerEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Input = table.Column<string>(type: "jsonb", nullable: true),
                    TriggerDetail = table.Column<string>(type: "jsonb", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CausationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Depth = table.Column<int>(type: "integer", nullable: false),
                    Result = table.Column<string>(type: "jsonb", nullable: true),
                    Error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Logs = table.Column<string>(type: "text", nullable: true),
                    Calls = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_runs_CorrelationId",
                schema: "functions",
                table: "runs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_runs_Status_NextAttemptAt",
                schema: "functions",
                table: "runs",
                columns: new[] { "Status", "NextAttemptAt" },
                filter: "\"Status\" IN ('Pending', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_runs_TenantId_EnvironmentId_InstanceId_CreatedAt",
                schema: "functions",
                table: "runs",
                columns: new[] { "TenantId", "EnvironmentId", "InstanceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_runs_TenantId_EnvironmentId_InstanceId_TriggerEventId_Funct~",
                schema: "functions",
                table: "runs",
                columns: new[] { "TenantId", "EnvironmentId", "InstanceId", "TriggerEventId", "Function" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runs",
                schema: "functions");
        }
    }
}
