using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.DynamicApps
{
    /// <inheritdoc />
    public partial class InitialApps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "apps");

            migrationBuilder.CreateTable(
                name: "apps",
                schema: "apps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DraftRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PublishedRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastRevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_apps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "changes",
                schema: "apps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seq = table.Column<int>(type: "integer", nullable: false),
                    Op = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Path = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    BeforeJson = table.Column<string>(type: "jsonb", nullable: true),
                    AfterJson = table.Column<string>(type: "jsonb", nullable: true),
                    ActorType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AiConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    AiRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToolCallId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_changes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "revisions",
                schema: "apps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    BasePublishedId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ValidatedHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SourceConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceAiRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_revisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_apps_TenantId_InstanceId",
                schema: "apps",
                table: "apps",
                columns: new[] { "TenantId", "InstanceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_changes_TenantId_RevisionId_Seq",
                schema: "apps",
                table: "changes",
                columns: new[] { "TenantId", "RevisionId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_revisions_TenantId_AppId_Number",
                schema: "apps",
                table: "revisions",
                columns: new[] { "TenantId", "AppId", "Number" },
                unique: true);

            // A revision's content is frozen once it leaves Draft (ADR 0021). Its status may still
            // move (Published -> Superseded / RolledBack); its snapshot and hash may not. Enforced
            // here so no code path -- a bug, a raw statement, a future refactor -- can quietly
            // rewrite what a published API was generated from. Deletes stay allowed: tenant purge.
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

                CREATE TRIGGER revisions_freeze BEFORE UPDATE ON apps.revisions
                    FOR EACH ROW EXECUTE FUNCTION apps.revisions_freeze();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS revisions_freeze ON apps.revisions;
                DROP FUNCTION IF EXISTS apps.revisions_freeze();
                """);

            migrationBuilder.DropTable(
                name: "apps",
                schema: "apps");

            migrationBuilder.DropTable(
                name: "changes",
                schema: "apps");

            migrationBuilder.DropTable(
                name: "revisions",
                schema: "apps");
        }
    }
}
