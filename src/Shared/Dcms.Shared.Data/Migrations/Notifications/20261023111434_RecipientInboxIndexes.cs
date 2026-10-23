using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Notifications
{
    /// <inheritdoc />
    public partial class RecipientInboxIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_recipients_TenantId_UserId_ReadAt_CreatedAt",
                schema: "notifications",
                table: "recipients");

            migrationBuilder.CreateIndex(
                name: "IX_recipients_inbox",
                schema: "notifications",
                table: "recipients",
                columns: new[] { "TenantId", "UserId", "CreatedAt" },
                descending: new[] { false, false, true },
                filter: "\"DismissedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_recipients_unread",
                schema: "notifications",
                table: "recipients",
                columns: new[] { "TenantId", "UserId" },
                filter: "\"ReadAt\" IS NULL AND \"DismissedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_recipients_inbox",
                schema: "notifications",
                table: "recipients");

            migrationBuilder.DropIndex(
                name: "IX_recipients_unread",
                schema: "notifications",
                table: "recipients");

            migrationBuilder.CreateIndex(
                name: "IX_recipients_TenantId_UserId_ReadAt_CreatedAt",
                schema: "notifications",
                table: "recipients",
                columns: new[] { "TenantId", "UserId", "ReadAt", "CreatedAt" });
        }
    }
}
