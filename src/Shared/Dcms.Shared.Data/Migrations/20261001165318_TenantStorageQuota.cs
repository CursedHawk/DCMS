using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class TenantStorageQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "StorageQuotaBytes",
                schema: "tenancy",
                table: "tenants",
                type: "bigint",
                nullable: false,
                // Existing tenants get the platform default, not a cap of zero.
                defaultValue: 5L * 1024 * 1024 * 1024);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StorageQuotaBytes",
                schema: "tenancy",
                table: "tenants");
        }
    }
}
