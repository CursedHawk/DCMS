using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Edge
{
    /// <inheritdoc />
    public partial class EdgeManagedReissueAndAttemptOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReissueRequestedAt",
                schema: "edge",
                table: "managed_certificates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReachedCa",
                schema: "edge",
                table: "managed_certificate_attempts",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReissueRequestedAt",
                schema: "edge",
                table: "managed_certificates");

            migrationBuilder.DropColumn(
                name: "ReachedCa",
                schema: "edge",
                table: "managed_certificate_attempts");
        }
    }
}
