using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Cms
{
    /// <inheritdoc />
    public partial class ContentItemsCollectionRecentIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_content_items_collection_recent",
                schema: "cms",
                table: "content_items",
                columns: new[] { "TenantId", "PluginInstanceId", "ContentType", "UpdatedAt", "Id" },
                descending: new[] { false, false, false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_content_items_collection_recent",
                schema: "cms",
                table: "content_items");
        }
    }
}
