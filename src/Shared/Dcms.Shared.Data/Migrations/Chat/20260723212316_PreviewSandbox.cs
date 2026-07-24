using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Chat
{
    /// <inheritdoc />
    public partial class PreviewSandbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_messages_TenantId_ConversationId_SentAt",
                schema: "chat",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "IX_conversations_TenantId_Status_LastMessageAt",
                schema: "chat",
                table: "conversations");

            migrationBuilder.AddColumn<bool>(
                name: "IsSandbox",
                schema: "chat",
                table: "messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSandbox",
                schema: "chat",
                table: "conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_messages_TenantId_IsSandbox_ConversationId_SentAt",
                schema: "chat",
                table: "messages",
                columns: new[] { "TenantId", "IsSandbox", "ConversationId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_TenantId_IsSandbox_Status_LastMessageAt",
                schema: "chat",
                table: "conversations",
                columns: new[] { "TenantId", "IsSandbox", "Status", "LastMessageAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_messages_TenantId_IsSandbox_ConversationId_SentAt",
                schema: "chat",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "IX_conversations_TenantId_IsSandbox_Status_LastMessageAt",
                schema: "chat",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "IsSandbox",
                schema: "chat",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "IsSandbox",
                schema: "chat",
                table: "conversations");

            migrationBuilder.CreateIndex(
                name: "IX_messages_TenantId_ConversationId_SentAt",
                schema: "chat",
                table: "messages",
                columns: new[] { "TenantId", "ConversationId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_TenantId_Status_LastMessageAt",
                schema: "chat",
                table: "conversations",
                columns: new[] { "TenantId", "Status", "LastMessageAt" });
        }
    }
}
