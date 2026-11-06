using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Functions
{
    /// <inheritdoc />
    public partial class DeploymentPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "Packages",
                schema: "functions",
                table: "deployments",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            // What a build installed is part of the build: as immutable as its bundles.
            migrationBuilder.Sql(Freeze("""OR NEW."Packages" IS DISTINCT FROM OLD."Packages" """));
        }

        private static string Freeze(string packages) => $$"""
            CREATE OR REPLACE FUNCTION functions.deployments_freeze() RETURNS trigger AS $$
            BEGIN
                IF OLD."Status" <> 'Building'
                   AND (NEW."Manifest" IS DISTINCT FROM OLD."Manifest" OR NEW."Bundles" IS DISTINCT FROM OLD."Bundles"
                        OR NEW."CommitSha" IS DISTINCT FROM OLD."CommitSha" {{packages}}) THEN
                    RAISE EXCEPTION 'functions.deployments %: a % deployment''s build is immutable', OLD."Id", OLD."Status"
                        USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                IF OLD."Status" NOT IN ('Building', 'Ready')
                   AND NEW."VouchedPermissions" IS DISTINCT FROM OLD."VouchedPermissions" THEN
                    RAISE EXCEPTION 'functions.deployments %: what a % deployment was vouched for is immutable', OLD."Id", OLD."Status"
                        USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                RETURN NEW;
            END
            $$ LANGUAGE plpgsql;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Freeze(""));
            migrationBuilder.DropColumn(
                name: "Packages",
                schema: "functions",
                table: "deployments");
        }
    }
}
