using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Data.Audit;

/// <summary>
/// Seals a finished month into <see cref="AuditChainAnchor"/> — the summary that outlives the
/// rows it describes.
///
/// <para>Retention drops whole monthly partitions. Once a partition is gone the chain it held
/// cannot be re-walked, so without an anchor the honest answer to "was last year intact?"
/// becomes "there is no way to know" — which is indistinguishable from "it was tampered with
/// and then deleted". The anchor keeps the first and last hash, the sequence range and the row
/// count, so a segment that was verified before it was dropped stays verified afterwards, and
/// a wholesale table rewrite remains detectable even against rows nobody can read any more.</para>
///
/// <para>The anchor carries its own HMAC over that summary, keyed from Vault. An attacker with
/// full database write access can delete an anchor — that is visible, a gap in the months — but
/// cannot forge one that agrees with a chain they rewrote. That asymmetry is the whole point:
/// see the note in <see cref="AuditSchemaConfigurator"/> about what the append-only controls do
/// and do not cover.</para>
///
/// <para>Idempotent. Sealing a month that is already sealed re-verifies it and leaves the
/// existing anchor alone, because a second anchor for one segment would make the pair
/// meaningless.</para>
/// </summary>
public sealed class AuditChainSealer(
    AuditDbContext db,
    AuditChainVerifier verifier,
    IAuditChainKeyProvider keys,
    ILogger<AuditChainSealer> logger)
{
    /// <summary>Version of the anchor HMAC input, so the format can change without invalidating old anchors.</summary>
    public const int AnchorVersion = 1;

    /// <summary>
    /// Seals every segment that has finished and is not yet sealed.
    ///
    /// <para>"Finished" means the period is strictly before the current month. A month still
    /// being written to has no final hash to anchor, and sealing it early would produce an
    /// anchor that every later record contradicts.</para>
    /// </summary>
    public async Task<int> SealCompletedAsync(CancellationToken ct = default)
    {
        var currentPeriod = AuditChainAppender.PeriodOf(DateTimeOffset.UtcNow);

        var candidates = await db.ChainHeads
            .AsNoTracking()
            .Where(h => h.Period < currentPeriod)
            .Select(h => new { h.ChainKey, h.Period })
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var sealedKeys = await db.ChainAnchors
            .AsNoTracking()
            .Where(a => a.Period < currentPeriod)
            .Select(a => new { a.ChainKey, a.Period })
            .ToListAsync(ct);

        var alreadySealed = sealedKeys
            .Select(a => (a.ChainKey, a.Period))
            .ToHashSet();

        var sealedNow = 0;

        foreach (var candidate in candidates)
        {
            if (alreadySealed.Contains((candidate.ChainKey, candidate.Period)))
            {
                continue;
            }

            if (await SealAsync(candidate.ChainKey, candidate.Period, ct))
            {
                sealedNow++;
            }
        }

        return sealedNow;
    }

    /// <summary>
    /// Seals one segment. Returns false when the segment does not verify — a broken chain is
    /// never anchored, because an anchor is a statement that the segment was intact, and
    /// signing that statement about a segment that is not would destroy the only thing the
    /// anchor is for.
    /// </summary>
    public async Task<bool> SealAsync(Guid chainKey, DateOnly period, CancellationToken ct = default)
    {
        var result = await verifier.VerifySegmentAsync(chainKey, period, ct);
        if (!result.Valid)
        {
            logger.LogCritical(
                "Refusing to seal audit segment {ChainKey}/{Period:yyyy-MM}: {Reason}",
                chainKey, period, result.Reason);
            return false;
        }

        var rows = await db.Events
            .AsNoTracking()
            .Where(e => e.ChainKey == chainKey && e.Period == period)
            .OrderBy(e => e.Seq)
            .Select(e => new { e.Seq, e.Hash })
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            // A head with no rows: the segment was created and nothing landed in it. Nothing to
            // anchor, and an anchor over an empty range would later look like data loss.
            return false;
        }

        var anchor = new AuditChainAnchor
        {
            ChainKey = chainKey,
            Period = period,
            FirstSeq = rows[0].Seq,
            LastSeq = rows[^1].Seq,
            FirstHash = rows[0].Hash,
            LastHash = rows[^1].Hash,
            RowCount = rows.Count,
            SealedAt = DateTimeOffset.UtcNow,
        };
        anchor.AnchorHmac = ComputeHmac(anchor, keys.GetKey());

        db.ChainAnchors.Add(anchor);
        await db.SaveChangesAsync(ct);

        // Also to the log, at Critical, which is not an error level here but a routing
        // instruction: this line leaves Postgres for stdout and whatever collects it. An anchor
        // that exists only in the database it attests to is not an off-box anchor.
        logger.LogCritical(
            "AUDIT ANCHOR {ChainKey}/{Period:yyyy-MM} seq {FirstSeq}-{LastSeq} rows {RowCount} hash {Hash} hmac {Hmac}",
            chainKey, period, anchor.FirstSeq, anchor.LastSeq, anchor.RowCount,
            Convert.ToHexString(anchor.LastHash), Convert.ToHexString(anchor.AnchorHmac));

        return true;
    }

    /// <summary>
    /// Re-checks an anchor's own HMAC. Detects an anchor that was edited to agree with a
    /// rewritten chain — the one move an attacker with database access would have to make, and
    /// the one they cannot make without the Vault key.
    /// </summary>
    public bool IsAuthentic(AuditChainAnchor anchor) =>
        CryptographicOperations.FixedTimeEquals(anchor.AnchorHmac, ComputeHmac(anchor, keys.GetKey()));

    /// <summary>
    /// Hand-written and length-prefixed, for the same reason as
    /// <see cref="AuditCanonicalizer"/>: property order in a serializer is not a stability
    /// contract, and an anchor whose input silently changed shape would stop verifying against
    /// itself. Append fields, never reorder or remove them; bump <see cref="AnchorVersion"/>
    /// when the meaning changes.
    /// </summary>
    private static byte[] ComputeHmac(AuditChainAnchor anchor, byte[] key)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);

        writer.Write(AnchorVersion);
        writer.Write(anchor.ChainKey.ToByteArray());
        writer.Write(anchor.Period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        writer.Write(anchor.FirstSeq);
        writer.Write(anchor.LastSeq);
        writer.Write(anchor.FirstHash.Length);
        writer.Write(anchor.FirstHash);
        writer.Write(anchor.LastHash.Length);
        writer.Write(anchor.LastHash);
        writer.Write(anchor.RowCount);
        writer.Flush();

        return HMACSHA256.HashData(key, buffer.ToArray());
    }
}
