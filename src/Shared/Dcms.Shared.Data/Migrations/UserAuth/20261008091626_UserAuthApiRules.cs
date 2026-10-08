using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.UserAuth
{
    /// <inheritdoc />
    public partial class UserAuthApiRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_rules",
                schema: "userauth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Access = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_rules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_rules_TenantId_InstanceId",
                schema: "userauth",
                table: "api_rules",
                columns: new[] { "TenantId", "InstanceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_rules",
                schema: "userauth");
        }
    }
}
