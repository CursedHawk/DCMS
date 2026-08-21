using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AuditContextOnForgejoOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContextJson",
                schema: "identity",
                table: "forgejo_sync_outbox",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContextJson",
                schema: "identity",
                table: "forgejo_sync_outbox");
        }
    }
}
