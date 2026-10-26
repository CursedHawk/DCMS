using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnvironmentMigrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "environment_baselines",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemsJson = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environment_baselines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "environment_migrations",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MigrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ChangesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Order = table.Column<long>(type: "bigint", nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AppliedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolutionsJson = table.Column<string>(type: "jsonb", nullable: false),
                    RolledBackAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environment_migrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "environment_policies",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ItemKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environment_policies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_environment_baselines_TenantId",
                schema: "tenancy",
                table: "environment_baselines",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_environment_migrations_TenantId_MigrationId",
                schema: "tenancy",
                table: "environment_migrations",
                columns: new[] { "TenantId", "MigrationId" });

            migrationBuilder.CreateIndex(
                name: "IX_environment_migrations_TenantId_Order",
                schema: "tenancy",
                table: "environment_migrations",
                columns: new[] { "TenantId", "Order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_environment_policies_TenantId_Kind_ItemKey",
                schema: "tenancy",
                table: "environment_policies",
                columns: new[] { "TenantId", "Kind", "ItemKey" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "environment_baselines",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "environment_migrations",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "environment_policies",
                schema: "tenancy");
        }
    }
}
