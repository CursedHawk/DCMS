using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.DynamicApps
{
    /// <inheritdoc />
    public partial class EnvironmentDimension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_unique_keys_TenantId_InstanceId_ConstraintId_Key",
                schema: "apps",
                table: "unique_keys");

            migrationBuilder.DropIndex(
                name: "IX_site_view_defaults_TenantId_InstanceId_TableId_UserId",
                schema: "apps",
                table: "site_view_defaults");

            migrationBuilder.DropIndex(
                name: "IX_revisions_TenantId_AppId_Number",
                schema: "apps",
                table: "revisions");

            migrationBuilder.DropIndex(
                name: "IX_relation_links_TenantId_InstanceId_RelationshipId_SourceId_~",
                schema: "apps",
                table: "relation_links");

            migrationBuilder.DropIndex(
                name: "IX_flow_schedules_TenantId_InstanceId_FlowId",
                schema: "apps",
                table: "flow_schedules");

            migrationBuilder.DropIndex(
                name: "IX_flow_runs_TenantId_InstanceId_TriggerEventId_FlowId_FlowHash",
                schema: "apps",
                table: "flow_runs");

            migrationBuilder.DropIndex(
                name: "IX_flow_run_steps_TenantId_RunId_StepId_Attempt",
                schema: "apps",
                table: "flow_run_steps");

            migrationBuilder.DropIndex(
                name: "IX_changes_TenantId_RevisionId_Seq",
                schema: "apps",
                table: "changes");

            migrationBuilder.DropIndex(
                name: "IX_apps_TenantId_InstanceId",
                schema: "apps",
                table: "apps");

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "apps",
                table: "unique_keys",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "apps",
                table: "site_views",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "apps",
                table: "site_view_defaults",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "apps",
                table: "revisions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "apps",
                table: "relation_links",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "apps",
                table: "records",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "apps",
                table: "outbox",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "apps",
                table: "flow_schedules",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "apps",
                table: "flow_runs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "apps",
                table: "flow_run_steps",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "apps",
                table: "changes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                schema: "apps",
                table: "apps",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Every row so far is its tenant's production (ADR 0028), whose id is the tenant's. No
            // default afterwards: a row written without an environment must fail, not vanish.
            migrationBuilder.Sql("""
                UPDATE "apps"."unique_keys" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "apps"."unique_keys" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "apps"."site_views" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "apps"."site_views" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "apps"."site_view_defaults" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "apps"."site_view_defaults" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "apps"."revisions" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "apps"."revisions" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "apps"."relation_links" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "apps"."relation_links" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "apps"."records" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "apps"."records" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "apps"."outbox" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "apps"."outbox" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "apps"."flow_schedules" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "apps"."flow_schedules" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "apps"."flow_runs" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "apps"."flow_runs" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "apps"."flow_run_steps" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "apps"."flow_run_steps" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "apps"."changes" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "apps"."changes" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                UPDATE "apps"."apps" SET "EnvironmentId" = "TenantId";
                ALTER TABLE "apps"."apps" ALTER COLUMN "EnvironmentId" DROP DEFAULT;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_unique_keys_TenantId_EnvironmentId_InstanceId_ConstraintId_~",
                schema: "apps",
                table: "unique_keys",
                columns: new[] { "TenantId", "EnvironmentId", "InstanceId", "ConstraintId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_site_view_defaults_TenantId_EnvironmentId_InstanceId_TableI~",
                schema: "apps",
                table: "site_view_defaults",
                columns: new[] { "TenantId", "EnvironmentId", "InstanceId", "TableId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_revisions_TenantId_EnvironmentId_AppId_Number",
                schema: "apps",
                table: "revisions",
                columns: new[] { "TenantId", "EnvironmentId", "AppId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_relation_links_TenantId_EnvironmentId_InstanceId_Relationsh~",
                schema: "apps",
                table: "relation_links",
                columns: new[] { "TenantId", "EnvironmentId", "InstanceId", "RelationshipId", "SourceId", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_flow_schedules_TenantId_EnvironmentId_InstanceId_FlowId",
                schema: "apps",
                table: "flow_schedules",
                columns: new[] { "TenantId", "EnvironmentId", "InstanceId", "FlowId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_flow_runs_TenantId_EnvironmentId_InstanceId_TriggerEventId_~",
                schema: "apps",
                table: "flow_runs",
                columns: new[] { "TenantId", "EnvironmentId", "InstanceId", "TriggerEventId", "FlowId", "FlowHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_flow_run_steps_TenantId_EnvironmentId_RunId_StepId_Attempt",
                schema: "apps",
                table: "flow_run_steps",
                columns: new[] { "TenantId", "EnvironmentId", "RunId", "StepId", "Attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_changes_TenantId_EnvironmentId_RevisionId_Seq",
                schema: "apps",
                table: "changes",
                columns: new[] { "TenantId", "EnvironmentId", "RevisionId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_apps_TenantId_EnvironmentId_InstanceId",
                schema: "apps",
                table: "apps",
                columns: new[] { "TenantId", "EnvironmentId", "InstanceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_unique_keys_TenantId_EnvironmentId_InstanceId_ConstraintId_~",
                schema: "apps",
                table: "unique_keys");

            migrationBuilder.DropIndex(
                name: "IX_site_view_defaults_TenantId_EnvironmentId_InstanceId_TableI~",
                schema: "apps",
                table: "site_view_defaults");

            migrationBuilder.DropIndex(
                name: "IX_revisions_TenantId_EnvironmentId_AppId_Number",
                schema: "apps",
                table: "revisions");

            migrationBuilder.DropIndex(
                name: "IX_relation_links_TenantId_EnvironmentId_InstanceId_Relationsh~",
                schema: "apps",
                table: "relation_links");

            migrationBuilder.DropIndex(
                name: "IX_flow_schedules_TenantId_EnvironmentId_InstanceId_FlowId",
                schema: "apps",
                table: "flow_schedules");

            migrationBuilder.DropIndex(
                name: "IX_flow_runs_TenantId_EnvironmentId_InstanceId_TriggerEventId_~",
                schema: "apps",
                table: "flow_runs");

            migrationBuilder.DropIndex(
                name: "IX_flow_run_steps_TenantId_EnvironmentId_RunId_StepId_Attempt",
                schema: "apps",
                table: "flow_run_steps");

            migrationBuilder.DropIndex(
                name: "IX_changes_TenantId_EnvironmentId_RevisionId_Seq",
                schema: "apps",
                table: "changes");

            migrationBuilder.DropIndex(
                name: "IX_apps_TenantId_EnvironmentId_InstanceId",
                schema: "apps",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "apps",
                table: "unique_keys");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "apps",
                table: "site_views");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "apps",
                table: "site_view_defaults");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "apps",
                table: "revisions");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "apps",
                table: "relation_links");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "apps",
                table: "records");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "apps",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "apps",
                table: "flow_schedules");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "apps",
                table: "flow_runs");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "apps",
                table: "flow_run_steps");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "apps",
                table: "changes");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                schema: "apps",
                table: "apps");

            migrationBuilder.CreateIndex(
                name: "IX_unique_keys_TenantId_InstanceId_ConstraintId_Key",
                schema: "apps",
                table: "unique_keys",
                columns: new[] { "TenantId", "InstanceId", "ConstraintId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_site_view_defaults_TenantId_InstanceId_TableId_UserId",
                schema: "apps",
                table: "site_view_defaults",
                columns: new[] { "TenantId", "InstanceId", "TableId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_revisions_TenantId_AppId_Number",
                schema: "apps",
                table: "revisions",
                columns: new[] { "TenantId", "AppId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_relation_links_TenantId_InstanceId_RelationshipId_SourceId_~",
                schema: "apps",
                table: "relation_links",
                columns: new[] { "TenantId", "InstanceId", "RelationshipId", "SourceId", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_flow_schedules_TenantId_InstanceId_FlowId",
                schema: "apps",
                table: "flow_schedules",
                columns: new[] { "TenantId", "InstanceId", "FlowId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_flow_runs_TenantId_InstanceId_TriggerEventId_FlowId_FlowHash",
                schema: "apps",
                table: "flow_runs",
                columns: new[] { "TenantId", "InstanceId", "TriggerEventId", "FlowId", "FlowHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_flow_run_steps_TenantId_RunId_StepId_Attempt",
                schema: "apps",
                table: "flow_run_steps",
                columns: new[] { "TenantId", "RunId", "StepId", "Attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_changes_TenantId_RevisionId_Seq",
                schema: "apps",
                table: "changes",
                columns: new[] { "TenantId", "RevisionId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_apps_TenantId_InstanceId",
                schema: "apps",
                table: "apps",
                columns: new[] { "TenantId", "InstanceId" },
                unique: true);
        }
    }
}
