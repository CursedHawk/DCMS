using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Sites
{
    /// <inheritdoc />
    public partial class SiteGitMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GitDefaultBranch",
                schema: "sites",
                table: "sites",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "GitProvisionedAt",
                schema: "sites",
                table: "sites",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitRepoFullName",
                schema: "sites",
                table: "sites",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GitDefaultBranch",
                schema: "sites",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "GitProvisionedAt",
                schema: "sites",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "GitRepoFullName",
                schema: "sites",
                table: "sites");
        }
    }
}
