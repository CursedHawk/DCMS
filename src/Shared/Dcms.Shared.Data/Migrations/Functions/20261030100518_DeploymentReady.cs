using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Functions
{
    /// <inheritdoc />
    public partial class DeploymentReady : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActivatedBy",
                schema: "functions",
                table: "deployments",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Diagnostics",
                schema: "functions",
                table: "deployments",
                type: "jsonb",
                nullable: true);

            // The build is frozen once it leaves Building; what the deployer vouched for once it
            // leaves Ready, where a person deploying it sets it (ADR 0029).
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION functions.deployments_freeze() RETURNS trigger AS $$
                BEGIN
                    IF OLD."Status" <> 'Building'
                       AND (NEW."Manifest" IS DISTINCT FROM OLD."Manifest" OR NEW."Bundles" IS DISTINCT FROM OLD."Bundles"
                            OR NEW."CommitSha" IS DISTINCT FROM OLD."CommitSha") THEN
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
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION functions.deployments_freeze() RETURNS trigger AS $$
                BEGIN
                    IF OLD."Status" <> 'Building'
                       AND (NEW."Manifest" IS DISTINCT FROM OLD."Manifest" OR NEW."Bundles" IS DISTINCT FROM OLD."Bundles"
                            OR NEW."CommitSha" IS DISTINCT FROM OLD."CommitSha"
                            OR NEW."VouchedPermissions" IS DISTINCT FROM OLD."VouchedPermissions") THEN
                        RAISE EXCEPTION 'functions.deployments %: a % deployment is immutable', OLD."Id", OLD."Status"
                            USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                    RETURN NEW;
                END
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.DropColumn(
                name: "ActivatedBy",
                schema: "functions",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "Diagnostics",
                schema: "functions",
                table: "deployments");
        }
    }
}
