using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.DynamicApps
{
    /// <inheritdoc />
    public partial class DropFlowRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "flow_run_steps",
                schema: "apps");

            migrationBuilder.DropTable(
                name: "flow_runs",
                schema: "apps");

            migrationBuilder.DropTable(
                name: "flow_schedules",
                schema: "apps");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "flow_run_steps",
                schema: "apps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    InputJson = table.Column<string>(type: "jsonb", nullable: true),
                    OutputJson = table.Column<string>(type: "jsonb", nullable: true),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StepId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flow_run_steps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "flow_runs",
                schema: "apps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CausationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Depth = table.Column<int>(type: "integer", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FlowApiName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FlowHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FlowId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerJson = table.Column<string>(type: "jsonb", nullable: false),
                    Writes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flow_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "flow_schedules",
                schema: "apps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    EveryMinutes = table.Column<int>(type: "integer", nullable: false),
                    FlowId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    NextRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flow_schedules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_flow_run_steps_TenantId_EnvironmentId_RunId_StepId_Attempt",
                schema: "apps",
                table: "flow_run_steps",
                columns: new[] { "TenantId", "EnvironmentId", "RunId", "StepId", "Attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_flow_runs_NextAttemptAt",
                schema: "apps",
                table: "flow_runs",
                column: "NextAttemptAt",
                filter: "\"Status\" IN ('Pending', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_flow_runs_TenantId_CorrelationId",
                schema: "apps",
                table: "flow_runs",
                columns: new[] { "TenantId", "CorrelationId" });

            migrationBuilder.CreateIndex(
                name: "IX_flow_runs_TenantId_EnvironmentId_InstanceId_TriggerEventId_~",
                schema: "apps",
                table: "flow_runs",
                columns: new[] { "TenantId", "EnvironmentId", "InstanceId", "TriggerEventId", "FlowId", "FlowHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_flow_runs_TenantId_InstanceId_CreatedAt",
                schema: "apps",
                table: "flow_runs",
                columns: new[] { "TenantId", "InstanceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_flow_schedules_NextRunAt",
                schema: "apps",
                table: "flow_schedules",
                column: "NextRunAt");

            migrationBuilder.CreateIndex(
                name: "IX_flow_schedules_TenantId_EnvironmentId_InstanceId_FlowId",
                schema: "apps",
                table: "flow_schedules",
                columns: new[] { "TenantId", "EnvironmentId", "InstanceId", "FlowId" },
                unique: true);
        }
    }
}
