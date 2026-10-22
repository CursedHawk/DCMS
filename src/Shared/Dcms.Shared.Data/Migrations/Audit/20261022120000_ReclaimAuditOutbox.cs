using Dcms.Shared.Data.Audit;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.Audit
{
    /// <summary>
    /// One-off: empties the audit outbox of what <c>AuditRetention.PruneOutboxAsync</c> now prunes
    /// hourly, then returns the space. Until 2026-10-22 nothing deleted from it, and on vps1 it was
    /// the largest table in the database (1.56 GB, 896k delivered rows since 2026-08-21).
    ///
    /// <para>The delete runs first so that <c>VACUUM FULL</c> rewrites a table of a week's rows, not
    /// a million: its exclusive lock — audited writes wait behind it — lasts seconds. No model
    /// change, so hand-written and without a designer snapshot.</para>
    /// </summary>
    [DbContext(typeof(AuditDbContext))]
    [Migration("20261022120000_ReclaimAuditOutbox")]
    public partial class ReclaimAuditOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM audit.audit_outbox WHERE "AppliedAt" < now() - interval '7 days';""");
            // VACUUM cannot run inside a transaction block.
            migrationBuilder.Sql("VACUUM FULL audit.audit_outbox;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
