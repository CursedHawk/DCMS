using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Audit
{
    /// <inheritdoc />
    public partial class InitialAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            // audit_events is hand-written because MigrationBuilder cannot express
            // PARTITION BY RANGE. It is mapped ExcludeFromMigrations in AuditDbContext, so EF
            // knows its shape for querying but emits no DDL for it — keep the two in step.
            //
            // Monthly partitions, and the month is part of the chain key, so each partition
            // holds a self-contained chain and retention can drop one whole without breaking
            // any chain that outlives it.
            //
            // The primary key and every unique index on the parent must include the partition
            // key: that is why the PK is ("OccurredAt","Id") and dedup is ("OccurredAt",
            // "EventId") rather than a plain unique EventId. Sound only because OccurredAt is
            // assigned by the producer and carried unchanged through retries — never re-stamped
            // at write time.
            //
            // The chain constraint ("ChainKey","Period","Seq") cannot be spelled that way: it
            // must not gain OccurredAt, or two rows could claim one Seq. So it lives as a
            // UNIQUE index on each partition instead — see EnsureChainIndexesAsync in
            // AuditSchemaConfigurator, which creates it for every partition and is what keeps
            // it in place for months created later. Per-partition uniqueness is global
            // uniqueness here because Period is the month of OccurredAt, so all rows of one
            // (ChainKey, Period) land in one partition. There is deliberately no chain index on
            // the parent: Postgres clones parent indexes onto every partition, which would sit
            // beside the unique one over the same columns and cost writes for nothing.
            migrationBuilder.Sql("""
                CREATE TABLE audit.audit_events (
                    "Id"               uuid          NOT NULL,
                    "EventId"          uuid          NOT NULL,
                    "OccurredAt"       timestamptz   NOT NULL,
                    "RecordedAt"       timestamptz   NOT NULL,
                    "ChainKey"         uuid          NOT NULL,
                    "Period"           date          NOT NULL,
                    "Seq"              bigint        NOT NULL,
                    "PrevHash"         bytea         NULL,
                    "Hash"             bytea         NOT NULL,
                    "HashVersion"      smallint      NOT NULL,
                    "TenantId"         uuid          NOT NULL,
                    "IsSandbox"        boolean       NOT NULL,
                    "Action"           varchar(96)   NOT NULL,
                    "Category"         varchar(24)   NOT NULL,
                    "Outcome"          varchar(16)   NOT NULL,
                    "Severity"         smallint      NOT NULL,
                    "ActorKind"        varchar(24)   NOT NULL,
                    "ActorId"          uuid          NULL,
                    "ActorRef"         varchar(200)  NULL,
                    "ActorDisplay"     varchar(200)  NULL,
                    "ActorAttribution" varchar(16)   NOT NULL,
                    "SubjectUserId"    uuid          NULL,
                    "ResourceType"     varchar(64)   NULL,
                    "ResourceId"       varchar(128)  NULL,
                    "ResourceLabel"    varchar(256)  NULL,
                    "ServiceName"      varchar(32)   NOT NULL,
                    "ServiceInstance"  varchar(64)   NOT NULL,
                    "ProducerSeq"      bigint        NOT NULL,
                    "CorrelationId"    varchar(64)   NULL,
                    "CausationId"      uuid          NULL,
                    "TraceId"          varchar(32)   NULL,
                    "SpanId"           varchar(16)   NULL,
                    "HttpMethod"       varchar(8)    NULL,
                    "RoutePattern"     varchar(256)  NULL,
                    "StatusCode"       integer       NULL,
                    "IpAddress"        varchar(45)   NULL,
                    "IpTrusted"        boolean       NOT NULL,
                    "UserAgent"        varchar(512)  NULL,
                    "MetadataJson"     jsonb         NOT NULL,
                    "ChangesJson"      jsonb         NULL,
                    "RedactionVersion" smallint      NOT NULL,
                    "SchemaVersion"    smallint      NOT NULL,
                    CONSTRAINT "PK_audit_events" PRIMARY KEY ("OccurredAt", "Id")
                ) PARTITION BY RANGE ("OccurredAt");
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "UX_audit_events_dedup"
                    ON audit.audit_events ("OccurredAt", "EventId");
                CREATE INDEX "IX_audit_events_tenant_time"
                    ON audit.audit_events ("TenantId", "OccurredAt" DESC);
                CREATE INDEX "IX_audit_events_tenant_action_time"
                    ON audit.audit_events ("TenantId", "Action", "OccurredAt" DESC);
                CREATE INDEX "IX_audit_events_resource"
                    ON audit.audit_events ("ResourceType", "ResourceId", "OccurredAt" DESC);
                CREATE INDEX "IX_audit_events_actor_time"
                    ON audit.audit_events ("ActorId", "OccurredAt" DESC) WHERE "ActorId" IS NOT NULL;
                CREATE INDEX "IX_audit_events_subject_time"
                    ON audit.audit_events ("SubjectUserId", "OccurredAt" DESC) WHERE "SubjectUserId" IS NOT NULL;
                CREATE INDEX "IX_audit_events_instance_seq"
                    ON audit.audit_events ("ServiceInstance", "ProducerSeq");
                """);

            // A DEFAULT partition is a deliberate safety net. Without one, a record whose
            // month has no partition yet fails to insert — and losing an audit record is far
            // worse than the operational chore this creates. The trap to know about: once the
            // default holds rows for a month, creating that month's partition fails until they
            // are moved. AuditSchemaConfigurator pre-creates months ahead so it stays empty.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS audit.audit_events_default
                    PARTITION OF audit.audit_events DEFAULT;

                CREATE UNIQUE INDEX IF NOT EXISTS "UX_audit_events_default_chain"
                    ON audit.audit_events_default ("ChainKey", "Period", "Seq");
                """);

            migrationBuilder.CreateTable(
                name: "audit_outbox",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "chain_anchors",
                schema: "audit",
                columns: table => new
                {
                    ChainKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Period = table.Column<DateOnly>(type: "date", nullable: false),
                    FirstSeq = table.Column<long>(type: "bigint", nullable: false),
                    LastSeq = table.Column<long>(type: "bigint", nullable: false),
                    FirstHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    LastHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    RowCount = table.Column<long>(type: "bigint", nullable: false),
                    SealedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AnchorHmac = table.Column<byte[]>(type: "bytea", nullable: false),
                    PartitionDroppedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chain_anchors", x => new { x.ChainKey, x.Period });
                });

            migrationBuilder.CreateTable(
                name: "chain_heads",
                schema: "audit",
                columns: table => new
                {
                    ChainKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Period = table.Column<DateOnly>(type: "date", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    LastHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chain_heads", x => new { x.ChainKey, x.Period });
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_outbox_EventId",
                schema: "audit",
                table: "audit_outbox",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_outbox_pending",
                schema: "audit",
                table: "audit_outbox",
                column: "Id",
                filter: "\"AppliedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_outbox",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "chain_anchors",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "chain_heads",
                schema: "audit");

            // Dropping the parent takes every partition with it.
            migrationBuilder.Sql("DROP TABLE IF EXISTS audit.audit_events CASCADE;");
        }
    }
}
