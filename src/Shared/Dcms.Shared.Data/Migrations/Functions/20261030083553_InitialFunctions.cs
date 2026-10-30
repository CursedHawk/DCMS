using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Functions
{
    /// <inheritdoc />
    public partial class InitialFunctions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "functions");

            migrationBuilder.CreateTable(
                name: "deployments",
                schema: "functions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Manifest = table.Column<string>(type: "jsonb", nullable: false),
                    Bundles = table.Column<string>(type: "jsonb", nullable: false),
                    VouchedPermissions = table.Column<List<string>>(type: "text[]", nullable: false),
                    Error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deployments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deployments_TenantId_EnvironmentId_InstanceId_Number",
                schema: "functions",
                table: "deployments",
                columns: new[] { "TenantId", "EnvironmentId", "InstanceId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_deployments_active",
                schema: "functions",
                table: "deployments",
                columns: new[] { "TenantId", "EnvironmentId", "InstanceId" },
                unique: true,
                filter: "\"Status\" = 'Active'");

            // A deployment is frozen once it leaves Building (ADR 0029): what ran, and what its
            // deployer vouched for, can be audited later. Its status may still move (Active ->
            // Superseded / RolledBack). Enforced here so no code path can rewrite what ran.
            // Deletes stay allowed: tenant purge.
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

                CREATE TRIGGER deployments_freeze BEFORE UPDATE ON functions.deployments
                    FOR EACH ROW EXECUTE FUNCTION functions.deployments_freeze();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS deployments_freeze ON functions.deployments;
                DROP FUNCTION IF EXISTS functions.deployments_freeze();
                """);

            migrationBuilder.DropTable(
                name: "deployments",
                schema: "functions");
        }
    }
}
