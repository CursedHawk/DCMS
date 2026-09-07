using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Notifications
{
    /// <inheritdoc />
    public partial class PlatformNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_notifications",
                schema: "notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    ParamsJson = table.Column<string>(type: "jsonb", nullable: false),
                    LinkPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ResourceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    DedupeKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "platform_notification_reads",
                schema: "notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DismissedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_notification_reads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_platform_notification_reads_platform_notifications_Notifica~",
                        column: x => x.NotificationId,
                        principalSchema: "notifications",
                        principalTable: "platform_notifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_platform_notification_reads_NotificationId_UserId",
                schema: "notifications",
                table: "platform_notification_reads",
                columns: new[] { "NotificationId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_notification_reads_UserId_ReadAt",
                schema: "notifications",
                table: "platform_notification_reads",
                columns: new[] { "UserId", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "IX_platform_notifications_CreatedAt",
                schema: "notifications",
                table: "platform_notifications",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_platform_notifications_DedupeKey",
                schema: "notifications",
                table: "platform_notifications",
                column: "DedupeKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_notification_reads",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "platform_notifications",
                schema: "notifications");
        }
    }
}
