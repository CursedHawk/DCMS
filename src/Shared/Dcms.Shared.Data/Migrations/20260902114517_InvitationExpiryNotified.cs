using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class InvitationExpiryNotified : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiredNotifiedAt",
                schema: "tenancy",
                table: "invitations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_invitations_AcceptedAt_ExpiredNotifiedAt_ExpiresAt",
                schema: "tenancy",
                table: "invitations",
                columns: new[] { "AcceptedAt", "ExpiredNotifiedAt", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_invitations_AcceptedAt_ExpiredNotifiedAt_ExpiresAt",
                schema: "tenancy",
                table: "invitations");

            migrationBuilder.DropColumn(
                name: "ExpiredNotifiedAt",
                schema: "tenancy",
                table: "invitations");
        }
    }
}
