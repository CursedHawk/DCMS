using Dcms.Shared.Audit;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Data.Audit;

/// <summary>
/// Drops audit partitions past their retention window.
///
/// <para>Dropping a whole partition, never deleting rows. A <c>DELETE</c> would fight the
/// append-only trigger, rewrite the table, and — the part that matters — be the same operation
/// an attacker would use to remove a record. <c>DROP TABLE</c> on a partition is a schema
/// change: coarse, all-or-nothing, and impossible to aim at one inconvenient row. Because the
/// chain key includes the period, each month is a self-contained chain, so removing one leaves
/// every other month verifiable exactly as before.</para>
///
/// <para><b>Nothing is dropped until it is sealed.</b> The anchor is what lets a dropped month
/// still be shown to have been intact; dropping first and anchoring never would convert
/// retention into evidence destruction. A month that fails to seal — because its chain does not
/// verify — is therefore never dropped either, which is the right way round: the segment that
/// most needs looking at is the one that stays.</para>
/// </summary>
public sealed class AuditRetention(
    AuditDbContext db,
    AuditChainSealer sealer,
    AuditOptions options,
    IAuditRecorder audit,
    ILogger<AuditRetention> logger)
{
    public async Task<int> ApplyAsync(CancellationToken ct = default)
    {
        // Seal first, always. The order is the whole safety property.
        var justSealed = await sealer.SealCompletedAsync(ct);
        if (justSealed > 0)
        {
            logger.LogInformation("Sealed {Count} audit segment(s).", justSealed);
        }

        var cutoff = AuditChainAppender.PeriodOf(DateTimeOffset.UtcNow.AddDays(-options.RetentionDays));

        // Only months every one of whose segments has an anchor. One unsealed tenant is enough
        // to keep the whole partition, because the partition is shared: dropping it would take
        // that tenant's unanchored records with it.
        var expired = await db.ChainHeads
            .AsNoTracking()
            .Where(h => h.Period < cutoff)
            .Select(h => h.Period)
            .Distinct()
            .ToListAsync(ct);

        var dropped = 0;

        foreach (var period in expired.OrderBy(p => p))
        {
            var unsealed = await db.ChainHeads
                .AsNoTracking()
                .Where(h => h.Period == period)
                .CountAsync(h => !db.ChainAnchors.Any(a => a.ChainKey == h.ChainKey && a.Period == h.Period), ct);

            if (unsealed > 0)
            {
                logger.LogWarning(
                    "Keeping audit partition for {Period:yyyy-MM}: {Count} segment(s) are not sealed. "
                    + "A segment that will not seal is one whose chain does not verify; look at it rather than dropping it.",
                    period, unsealed);
                continue;
            }

            if (await DropPartitionAsync(period, ct))
            {
                dropped++;
            }
        }

        return dropped;
    }

    private async Task<bool> DropPartitionAsync(DateOnly period, CancellationToken ct)
    {
        var name = $"audit_events_{period:yyyy}m{period:MM}";

        // Built into a local first, which is also what stops EF1002 firing: a Postgres
        // identifier cannot be parameterised, so the name has to be interpolated. It comes
        // from a DateOnly format string and nothing else, so there is no caller-supplied
        // text anywhere near this SQL.
        var sql = $"""
                   ALTER TABLE audit.audit_events DETACH PARTITION audit."{name}";
                   DROP TABLE audit."{name}";
                   """;

        try
        {
            // Detach before dropping so a reader mid-query against the parent sees a clean
            // partition set rather than a table disappearing underneath it.
            await db.Database.ExecuteSqlRawAsync(sql, ct);
        }
        catch (Exception ex)
        {
            // Most likely the partition was never created — a month with no records at all.
            logger.LogWarning(ex, "Could not drop audit partition {Partition}.", name);
            return false;
        }

        await db.ChainAnchors
            .Where(a => a.Period == period)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.PartitionDroppedAt, DateTimeOffset.UtcNow), ct);

        // The chain heads go too: they are the writer's working state for a month that no
        // longer exists. The anchors are what remain, and they are never dropped.
        await db.ChainHeads.Where(h => h.Period == period).ExecuteDeleteAsync(ct);

        logger.LogWarning(
            "Dropped audit partition {Partition} ({Days}-day retention). Its anchors are retained.",
            name, options.RetentionDays);

        // The audit log recording its own pruning. It belongs in the log for the same reason
        // everything else does — a month of history disappearing is an event, and the only
        // alternative account of it is a log line nobody keeps for four hundred days.
        audit.Record(AuditActions.AuditPartitionDropped)
            .Platform()
            .For("audit_partition", name)
            .As(AuditCategory.System, AuditSeverity.Notice)
            .With("period", period.ToString("yyyy-MM", CultureInfo.InvariantCulture))
            .With("retention_days", options.RetentionDays);
        await audit.FlushAsync(ct);

        return true;
    }
}
