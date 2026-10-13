using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Identity.Migrations
{
    /// <inheritdoc />
    public partial class BrowserAccountSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SelectedAt",
                schema: "identity",
                table: "browser_accounts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Rows from before the column: the most recently used one is the best guess at the
            // account each browser is on, and the next sign-in settles it for real.
            migrationBuilder.Sql("""UPDATE identity.browser_accounts SET "SelectedAt" = "LastUsedAt";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SelectedAt",
                schema: "identity",
                table: "browser_accounts");
        }
    }
}
