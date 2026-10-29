using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnvironmentSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReadyAt",
                schema: "tenancy",
                table: "environments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SetupJson",
                schema: "tenancy",
                table: "environments",
                type: "jsonb",
                nullable: true);

            // Every environment that exists was set up already.
            migrationBuilder.Sql("""UPDATE tenancy.environments SET "ReadyAt" = "CreatedAt";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReadyAt",
                schema: "tenancy",
                table: "environments");

            migrationBuilder.DropColumn(
                name: "SetupJson",
                schema: "tenancy",
                table: "environments");
        }
    }
}
