using Dcms.Shared.Data.DynamicApps;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.DynamicApps
{
    /// <summary>
    /// One-off: returns the space <c>apps.outbox</c> grew to before it was pruned. On vps1 it held
    /// 4,961 rows in 608 MB, the bloat of the 2026-10-11 stress import's 116k-event backlog; plain
    /// VACUUM reuses that space but never gives it back, and it competes for page cache. The table
    /// is small, so the exclusive lock lasts seconds. No model change, so hand-written.
    /// </summary>
    [DbContext(typeof(AppsDbContext))]
    [Migration("20261022120000_ReclaimAppsOutbox")]
    public partial class ReclaimAppsOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // VACUUM cannot run inside a transaction block.
            migrationBuilder.Sql("VACUUM FULL apps.outbox;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
