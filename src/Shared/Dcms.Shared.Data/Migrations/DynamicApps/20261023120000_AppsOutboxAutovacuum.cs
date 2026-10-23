using Dcms.Shared.Data.DynamicApps;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.DynamicApps
{
    /// <summary>
    /// Vacuums <c>apps.outbox</c> after a fixed 2,000 dead rows instead of a fifth of the table: each
    /// row is updated once when sent, and the dispatcher's poll walked the dead entries to find an
    /// empty queue, 2.4 ms a poll (load test 2026-10-23). The audit outbox has the same change. No
    /// model change, so hand-written.
    /// </summary>
    [DbContext(typeof(AppsDbContext))]
    [Migration("20261023120000_AppsOutboxAutovacuum")]
    public partial class AppsOutboxAutovacuum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE apps.outbox SET (autovacuum_vacuum_scale_factor = 0, autovacuum_vacuum_threshold = 2000);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE apps.outbox RESET (autovacuum_vacuum_scale_factor, autovacuum_vacuum_threshold);");
        }
    }
}
