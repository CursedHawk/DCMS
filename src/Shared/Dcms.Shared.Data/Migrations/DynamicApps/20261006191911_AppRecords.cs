using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.DynamicApps
{
    /// <inheritdoc />
    public partial class AppRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "records",
                schema: "apps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TableId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    OwnerVisitorId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "relation_links",
                schema: "apps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationshipId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relation_links", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "unique_keys",
                schema: "apps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConstraintId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_unique_keys", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_records_Data",
                schema: "apps",
                table: "records",
                column: "Data")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "jsonb_path_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_records_TenantId_InstanceId_TableId_CreatedAt",
                schema: "apps",
                table: "records",
                columns: new[] { "TenantId", "InstanceId", "TableId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_relation_links_TenantId_InstanceId_RelationshipId_SourceId_~",
                schema: "apps",
                table: "relation_links",
                columns: new[] { "TenantId", "InstanceId", "RelationshipId", "SourceId", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_relation_links_TenantId_InstanceId_RelationshipId_TargetId",
                schema: "apps",
                table: "relation_links",
                columns: new[] { "TenantId", "InstanceId", "RelationshipId", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_unique_keys_TenantId_InstanceId_ConstraintId_Key",
                schema: "apps",
                table: "unique_keys",
                columns: new[] { "TenantId", "InstanceId", "ConstraintId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_unique_keys_TenantId_RecordId",
                schema: "apps",
                table: "unique_keys",
                columns: new[] { "TenantId", "RecordId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "records",
                schema: "apps");

            migrationBuilder.DropTable(
                name: "relation_links",
                schema: "apps");

            migrationBuilder.DropTable(
                name: "unique_keys",
                schema: "apps");
        }
    }
}
