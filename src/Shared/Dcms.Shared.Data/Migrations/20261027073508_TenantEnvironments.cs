using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class TenantEnvironments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "environments",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PromotesFrom = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    ForkedFrom = table.Column<Guid>(type: "uuid", nullable: true),
                    MergesOwnChangesOnly = table.Column<bool>(type: "boolean", nullable: false),
                    DeleteWhenMerged = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_environments_TenantId_Name",
                schema: "tenancy",
                table: "environments",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            // Every tenant's production environment (ADR 0028), whose id is the tenant's, so
            // every row already written is production's without being touched.
            migrationBuilder.Sql("""
                INSERT INTO tenancy.environments ("Id", "TenantId", "Name", "Order", "Purpose", "PromotesFrom",
                                                  "MergesOwnChangesOnly", "DeleteWhenMerged", "CreatedAt")
                SELECT t."Id"::uuid, t."Id"::uuid, 'production', 0, 'Stage', '{}'::uuid[], false, false, now()
                FROM tenancy.tenants t
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "environments",
                schema: "tenancy");
        }
    }
}
