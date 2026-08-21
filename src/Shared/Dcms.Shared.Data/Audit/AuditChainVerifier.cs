using Dcms.Shared.Audit;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Audit;

/// <param name="Checked">How many rows were walked.</param>
/// <param name="FirstBadSeq">Where the chain first stops adding up, if it does.</param>
/// <param name="Reason">
/// Human-readable explanation, phrased for whoever is investigating rather than for a log parser.
/// </param>
public sealed record AuditChainSegmentResult(
    Guid ChainKey,
    DateOnly Period,
    long Checked,
    bool Valid,
    long? FirstBadSeq,
    string? Reason);

/// <summary>
/// Recomputes a chain and reports where — if anywhere — it stops adding up.
///
/// <para>Verification walks by <c>Seq</c>, never by <c>OccurredAt</c>. Seq is writer-arrival
/// order; a record produced earlier can be appended later after a retry, so the two orders
/// legitimately disagree and a verifier that assumed otherwise would report tampering on a
/// healthy system.</para>
/// </summary>
public sealed class AuditChainVerifier(AuditDbContext db, IAuditChainKeyProvider keys, AuditMetrics metrics)
{
    public async Task<AuditChainSegmentResult> VerifySegmentAsync(
        Guid chainKey,
        DateOnly period,
        CancellationToken cancellationToken = default)
    {
        var key = keys.GetKey();

        var rows = await db.Events
            .AsNoTracking()
            .Where(e => e.ChainKey == chainKey && e.Period == period)
            .OrderBy(e => e.Seq)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return new AuditChainSegmentResult(chainKey, period, 0, true, null, null);
        }

        byte[]? previousHash = null;
        long expectedSeq = 1;

        foreach (var row in rows)
        {
            if (row.Seq != expectedSeq)
            {
                return Broken(
                    chainKey, period, expectedSeq - 1, row.Seq,
                    $"Sequence jumps from {expectedSeq - 1} to {row.Seq}: {row.Seq - expectedSeq} record(s) are missing from this segment.");
            }

            // A row whose stored version differs from the current one was written under an
            // older field list; recompute with that version rather than declaring it broken.
            if (row.HashVersion != AuditCanonicalizer.CurrentVersion)
            {
                return Broken(
                    chainKey, period, expectedSeq - 1, row.Seq,
                    $"Record was hashed under algorithm version {row.HashVersion}; this build implements version {AuditCanonicalizer.CurrentVersion}. "
                    + "It cannot be checked either way — this is not evidence that the record was altered.");
            }

            var expectedPrev = expectedSeq == 1 ? null : previousHash;
            if (!ByteArraysEqual(row.PrevHash, expectedPrev))
            {
                return Broken(
                    chainKey, period, expectedSeq - 1, row.Seq,
                    "Record does not link to its predecessor: a record was removed or reordered.");
            }

            var recomputed = AuditCanonicalizer.ComputeHash(row, row.PrevHash, key);
            if (!ByteArraysEqual(recomputed, row.Hash))
            {
                return Broken(
                    chainKey, period, expectedSeq - 1, row.Seq,
                    "Record contents do not match its hash: this row was altered after it was written.");
            }

            previousHash = row.Hash;
            expectedSeq++;
        }

        return new AuditChainSegmentResult(chainKey, period, rows.Count, true, null, null);
    }

    /// <summary>Verifies every segment of one tenant's chain, newest month first.</summary>
    public async Task<IReadOnlyList<AuditChainSegmentResult>> VerifyTenantAsync(
        Guid chainKey,
        CancellationToken cancellationToken = default)
    {
        var periods = await db.ChainHeads
            .AsNoTracking()
            .Where(h => h.ChainKey == chainKey)
            .OrderByDescending(h => h.Period)
            .Select(h => h.Period)
            .ToListAsync(cancellationToken);

        var results = new List<AuditChainSegmentResult>(periods.Count);
        foreach (var period in periods)
        {
            results.Add(await VerifySegmentAsync(chainKey, period, cancellationToken));
        }
        return results;
    }

    /// <summary>
    /// Every failing exit goes through here, so a broken segment cannot be discovered without
    /// also being counted. A verify that reports tampering to one operator's screen and
    /// nowhere else is how a finding gets lost.
    /// </summary>
    private AuditChainSegmentResult Broken(
        Guid chainKey,
        DateOnly period,
        long walked,
        long badSeq,
        string reason)
    {
        metrics.RecordChainBroken();
        return new AuditChainSegmentResult(chainKey, period, walked, false, badSeq, reason);
    }

    private static bool ByteArraysEqual(byte[]? left, byte[]? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }
        return left.AsSpan().SequenceEqual(right);
    }
}
