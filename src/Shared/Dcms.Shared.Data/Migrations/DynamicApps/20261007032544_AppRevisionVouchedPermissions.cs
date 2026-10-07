using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.DynamicApps
{
    /// <inheritdoc />
    public partial class AppRevisionVouchedPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "VouchedPermissions",
                schema: "apps",
                table: "revisions",
                type: "text[]",
                nullable: false,
                // Revisions published before this column vouched for nothing: their provider
                // actions stop until the app is published again by someone who may use them.
                defaultValueSql: "'{}'");

            // What the publisher vouched for is as fixed as what they published.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION apps.revisions_freeze() RETURNS trigger AS $$
                BEGIN
                    IF OLD."Status" <> 'Draft'
                       AND (NEW."Snapshot" IS DISTINCT FROM OLD."Snapshot" OR NEW."Hash" IS DISTINCT FROM OLD."Hash"
                            OR NEW."VouchedPermissions" IS DISTINCT FROM OLD."VouchedPermissions") THEN
                        RAISE EXCEPTION 'apps.revisions %: a % revision is immutable', OLD."Id", OLD."Status"
                            USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                    RETURN NEW;
                END
                $$ LANGUAGE plpgsql;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION apps.revisions_freeze() RETURNS trigger AS $$
                BEGIN
                    IF OLD."Status" <> 'Draft'
                       AND (NEW."Snapshot" IS DISTINCT FROM OLD."Snapshot" OR NEW."Hash" IS DISTINCT FROM OLD."Hash") THEN
                        RAISE EXCEPTION 'apps.revisions %: a % revision is immutable', OLD."Id", OLD."Status"
                            USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                    RETURN NEW;
                END
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.DropColumn(
                name: "VouchedPermissions",
                schema: "apps",
                table: "revisions");
        }
    }
}
