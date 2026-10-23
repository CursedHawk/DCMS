using Dcms.Shared.Data.Audit;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Audit
{
    /// <summary>
    /// Vacuums <c>audit.audit_outbox</c> after a fixed 2,000 dead rows instead of a fifth of the
    /// table. Every row is updated once when the chain writer applies it, and the week of delivered
    /// rows kept since 2026-10-22 puts the default trigger near 95,000 dead rows: the writer's claim
    /// then walked 34,361 dead entries of its pending index to find an empty queue, 3.9 ms a poll
    /// (load test 2026-10-23). No model change, so hand-written.
    /// </summary>
    [DbContext(typeof(AuditDbContext))]
    [Migration("20261023120000_AuditOutboxAutovacuum")]
    public partial class AuditOutboxAutovacuum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE audit.audit_outbox SET (autovacuum_vacuum_scale_factor = 0, autovacuum_vacuum_threshold = 2000);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE audit.audit_outbox RESET (autovacuum_vacuum_scale_factor, autovacuum_vacuum_threshold);");
        }
    }
}
