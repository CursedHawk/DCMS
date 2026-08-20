using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Analytics
{
    /// <inheritdoc />
    public partial class AnalyticsDimensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Browser",
                schema: "analytics",
                table: "events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Country",
                schema: "analytics",
                table: "events",
                type: "character(2)",
                fixedLength: true,
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Device",
                schema: "analytics",
                table: "events",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Os",
                schema: "analytics",
                table: "events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UtmCampaign",
                schema: "analytics",
                table: "events",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UtmMedium",
                schema: "analytics",
                table: "events",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UtmSource",
                schema: "analytics",
                table: "events",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_events_TenantId_OccurredAt_VisitorHash",
                schema: "analytics",
                table: "events",
                columns: new[] { "TenantId", "OccurredAt", "VisitorHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_events_TenantId_OccurredAt_VisitorHash",
                schema: "analytics",
                table: "events");

            migrationBuilder.DropColumn(
                name: "Browser",
                schema: "analytics",
                table: "events");

            migrationBuilder.DropColumn(
                name: "Country",
                schema: "analytics",
                table: "events");

            migrationBuilder.DropColumn(
                name: "Device",
                schema: "analytics",
                table: "events");

            migrationBuilder.DropColumn(
                name: "Os",
                schema: "analytics",
                table: "events");

            migrationBuilder.DropColumn(
                name: "UtmCampaign",
                schema: "analytics",
                table: "events");

            migrationBuilder.DropColumn(
                name: "UtmMedium",
                schema: "analytics",
                table: "events");

            migrationBuilder.DropColumn(
                name: "UtmSource",
                schema: "analytics",
                table: "events");
        }
    }
}
