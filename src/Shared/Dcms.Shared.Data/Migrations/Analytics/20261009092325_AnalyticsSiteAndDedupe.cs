using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Analytics
{
    /// <inheritdoc />
    public partial class AnalyticsSiteAndDedupe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_daily_rollups",
                schema: "analytics",
                table: "daily_rollups");

            migrationBuilder.AddColumn<Guid>(
                name: "EventId",
                schema: "analytics",
                table: "events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Hostname",
                schema: "analytics",
                table: "events",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SiteId",
                schema: "analytics",
                table: "events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SiteId",
                schema: "analytics",
                table: "daily_rollups",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_daily_rollups",
                schema: "analytics",
                table: "daily_rollups",
                columns: new[] { "TenantId", "Day", "SiteId", "Type", "Path" });

            migrationBuilder.CreateIndex(
                name: "IX_events_TenantId_EventId",
                schema: "analytics",
                table: "events",
                columns: new[] { "TenantId", "EventId" },
                unique: true,
                filter: "\"EventId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_events_TenantId_SiteId_OccurredAt",
                schema: "analytics",
                table: "events",
                columns: new[] { "TenantId", "SiteId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_events_TenantId_EventId",
                schema: "analytics",
                table: "events");

            migrationBuilder.DropIndex(
                name: "IX_events_TenantId_SiteId_OccurredAt",
                schema: "analytics",
                table: "events");

            migrationBuilder.DropPrimaryKey(
                name: "PK_daily_rollups",
                schema: "analytics",
                table: "daily_rollups");

            migrationBuilder.DropColumn(
                name: "EventId",
                schema: "analytics",
                table: "events");

            migrationBuilder.DropColumn(
                name: "Hostname",
                schema: "analytics",
                table: "events");

            migrationBuilder.DropColumn(
                name: "SiteId",
                schema: "analytics",
                table: "events");

            migrationBuilder.DropColumn(
                name: "SiteId",
                schema: "analytics",
                table: "daily_rollups");

            migrationBuilder.AddPrimaryKey(
                name: "PK_daily_rollups",
                schema: "analytics",
                table: "daily_rollups",
                columns: new[] { "TenantId", "Day", "Type", "Path" });
        }
    }
}
