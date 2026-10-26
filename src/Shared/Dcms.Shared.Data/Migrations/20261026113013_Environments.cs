using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class Environments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EnvironmentName",
                schema: "tenancy",
                table: "tenants",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EnvironmentOrder",
                schema: "tenancy",
                table: "tenants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<List<Guid>>(
                name: "PromotesFrom",
                schema: "tenancy",
                table: "tenants",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<Guid>(
                name: "WorkspaceId",
                schema: "tenancy",
                table: "tenants",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenants_WorkspaceId",
                schema: "tenancy",
                table: "tenants",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tenants_WorkspaceId",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "EnvironmentName",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "EnvironmentOrder",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "PromotesFrom",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                schema: "tenancy",
                table: "tenants");
        }
    }
}
