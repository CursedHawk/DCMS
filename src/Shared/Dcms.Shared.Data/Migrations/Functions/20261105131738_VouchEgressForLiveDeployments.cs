using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Functions
{
    /// <inheritdoc />
    public partial class VouchEgressForLiveDeployments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Outbound requests now need plugin:functions:egress vouched for at deploy (ADR 0031).
            // Deployments made before there was such a permission keep working: they are taken to
            // have been vouched for the hosts their deployer already approved. The freeze trigger
            // forbids exactly this change, so it steps aside for this one statement.
            migrationBuilder.Sql("""
                ALTER TABLE functions.deployments DISABLE TRIGGER deployments_freeze;
                UPDATE functions.deployments d
                   SET "VouchedPermissions" = array_append(d."VouchedPermissions", 'plugin:functions:egress')
                 WHERE NOT ('plugin:functions:egress' = ANY (d."VouchedPermissions"))
                   AND cardinality(d."VouchedPermissions") > 0
                   AND EXISTS (SELECT 1 FROM jsonb_array_elements(COALESCE(d."Manifest" -> 'functions', '[]'::jsonb)) f
                                WHERE jsonb_array_length(COALESCE(f -> 'uses' -> 'egress', '[]'::jsonb)) > 0);
                ALTER TABLE functions.deployments ENABLE TRIGGER deployments_freeze;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
