using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Sites
{
    /// <inheritdoc />
    public partial class AddStaticFilesBundle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StaticBundleFileCount",
                schema: "sites",
                table: "sites",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StaticBundleKey",
                schema: "sites",
                table: "sites",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StaticBundleName",
                schema: "sites",
                table: "sites",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StaticBundleSize",
                schema: "sites",
                table: "sites",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StaticBundleUploadedAt",
                schema: "sites",
                table: "sites",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StaticBundleFileCount",
                schema: "sites",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "StaticBundleKey",
                schema: "sites",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "StaticBundleName",
                schema: "sites",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "StaticBundleSize",
                schema: "sites",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "StaticBundleUploadedAt",
                schema: "sites",
                table: "sites");
        }
    }
}
