using Dcms.Shared.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Data.Audit;

/// <summary>One process's sequence range, and what is missing from it.</summary>
/// <param name="ServiceInstance">The process identity, minted at its startup.</param>
/// <param name="Missing">
/// How many sequence numbers that process issued but never delivered. Zero is the expected
/// answer and the only reassuring one.
/// </param>
public sealed record AuditProducerGap(
    string ServiceInstance,
    string ServiceName,
    long FirstSeq,
    long LastSeq,
    long Delivered,
    long Missing);

/// <summary>
/// Finds records that were never written.
///
/// <para><b>This is the failure a hash chain cannot see.</b> The chain proves that the records
/// present were not altered or reordered; it says nothing whatsoever about a record that never
/// arrived. Deleting the last N entries of a chain leaves a perfectly valid chain. So does a
/// process being killed between recording and flushing, or a sink failing and the buffer being
/// dropped — and those are the failures that actually happen.</para>
///
/// <para>Every process stamps each record with its own instance id and a monotonic counter.
/// A counter that runs 1..500 but delivers 499 records means one is gone, and says which
/// process lost it and roughly when. That is the omission detector this system needs, and it
/// costs one index.</para>
///
/// <para>It cannot distinguish "lost" from "still in flight": a process that has recorded but
/// not yet flushed looks briefly like a gap at its own tail. Callers should therefore ignore a
/// shortfall of one or two on an instance that is still running, and treat a gap on a
/// <i>retired</i> instance as real.</para>
/// </summary>
public sealed class AuditGapDetector(AuditDbContext db, ILogger<AuditGapDetector> logger)
{
    /// <summary>
    /// Examines the producers active in a window. Scoped by time rather than run over the whole
    /// table because the question is always "did we lose anything lately" — an answer about
    /// last March arrives far too late to act on.
    /// </summary>
    /// <param name="until">
    /// Stop short of the present. A live process that has taken a sequence number but not yet
    /// flushed looks momentarily like a gap at its own tail, so callers should hold the window
    /// back by more than a flush takes and let the next pass cover the remainder. Windows are
    /// meant to overlap; nothing is skipped by lagging the upper bound.
    /// </param>
    public async Task<IReadOnlyList<AuditProducerGap>> FindAsync(
        DateTimeOffset since,
        DateTimeOffset? until = null,
        CancellationToken ct = default)
    {
        var upper = until ?? DateTimeOffset.MaxValue;

        var producers = await db.Events
            .AsNoTracking()
            .Where(e => e.OccurredAt >= since && e.OccurredAt <= upper)
            .GroupBy(e => new { e.ServiceInstance, e.ServiceName })
            .Select(g => new
            {
                g.Key.ServiceInstance,
                g.Key.ServiceName,
                First = g.Min(e => e.ProducerSeq),
                Last = g.Max(e => e.ProducerSeq),
                Delivered = (long)g.Count(),
            })
            .ToListAsync(ct);

        var gaps = new List<AuditProducerGap>();

        foreach (var p in producers)
        {
            // The counter is dense by construction, so the expected count is simply the range.
            var expected = p.Last - p.First + 1;
            var missing = expected - p.Delivered;

            if (missing <= 0)
            {
                continue;
            }

            gaps.Add(new AuditProducerGap(
                p.ServiceInstance, p.ServiceName, p.First, p.Last, p.Delivered, missing));

            logger.LogCritical(
                "Audit gap: {Service} instance {Instance} issued sequence {First}-{Last} but only {Delivered} "
                + "record(s) arrived — {Missing} were never written. A chain over the survivors still verifies; "
                + "that is exactly why this check exists.",
                p.ServiceName, p.ServiceInstance, p.First, p.Last, p.Delivered, missing);
        }

        return gaps;
    }
}
