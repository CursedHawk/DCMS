using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations
{
    /// <summary>
    /// ADR 0028 retires tenancy.tenants' ADR 0026 columns (WorkspaceId, EnvironmentName,
    /// EnvironmentOrder, PromotesFrom) from the model. Deliberately empty: the columns are read by
    /// <c>EnvironmentConversion</c>, which runs after every context has migrated and drops them
    /// once it has folded each environment tenant into its workspace. Dropping them here, before
    /// the other schemas have their environment columns, would lose what the conversion needs.
    /// </summary>
    public partial class EnvironmentTenantColumnsRetired : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
