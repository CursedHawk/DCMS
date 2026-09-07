using Dcms.Shared.Data.Edge;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Edge.Certificates;

/// <summary>Whether a managed certificate may be ordered right now, and if not, why and until when.</summary>
/// <param name="Allowed">False when a rate-limit ceiling is currently holding the order back.</param>
/// <param name="Reason">A sentence for the log and the console. Null when allowed.</param>
/// <param name="RetryAfter">When the ceiling will have decayed enough to try again.</param>
/// <param name="IssuancesRemaining">How many of the weekly issuances are left, for the console.</param>
public sealed record ManagedCertificateBudget(
    bool Allowed, string? Reason, DateTimeOffset? RetryAfter, int IssuancesRemaining);

/// <summary>
/// The rate-limit ceiling for managed certificates, and the reason it is not the existing
/// per-hostname backoff.
///
/// <para><b>The blast radius changed shape.</b> Per-hostname issuance spread rate-limit risk
/// across hostnames: a tenant whose DNS broke burned their own budget and nobody else's. One
/// certificate covering <c>highgeek.eu</c>, <c>*.highgeek.eu</c> and <c>*.dcms.highgeek.eu</c>
/// concentrates it — every hostname the platform serves now depends on the same identifier set
/// staying inside the same limits.</para>
///
/// <para><b>Why not <c>ConsecutiveFailures</c>.</b> That counter is deliberately cleared on every
/// process start by <see cref="EdgeTlsPreflight"/>, so an operator who fixes a DNS record and
/// restarts gets an immediate retry instead of sitting out a backoff they no longer deserve.
/// Correct for a hostname; catastrophic for a weekly budget, because a crash-looping container
/// would clear the counter and re-order on every boot and spend the whole week's allowance in
/// minutes. Nothing clears these rows.</para>
///
/// <para><b>Two ceilings, because Let's Encrypt has two limits and they behave differently.</b>
/// The duplicate-certificate limit — 5 per identical identifier set per 7 days, which renewals
/// are <i>not</i> exempt from — counts certificates actually issued, and blocks for a week. The
/// failed-authorization limit — 5 per identifier per account per hour — counts refusals and
/// decays hourly. Counting both against one weekly ceiling would mean three transient DNS
/// failures on the day the wildcard is first ordered locked the platform's main certificate out
/// for a week; counting only successes would let a validation-failure loop run unbounded. So
/// successes are counted weekly and failures hourly, each against the limit it actually
/// protects.</para>
///
/// <para>An attempt the CA never heard about — no Cloudflare token, Vault unreachable — counts
/// against neither ceiling, for the same reason it is not backed off: nothing was spent. It is
/// still <i>written down</i>, with <c>ReachedCa = false</c>, because "spent nothing" and "say
/// nothing" are different decisions and only the first was intended. See
/// <see cref="CertificateIssuanceUnavailableException"/>.</para>
/// </summary>
public static class ManagedCertificateGuard
{
    /// <summary>
    /// The identifier set as the CA counts it: normalised, de-duplicated and <b>sorted</b>.
    ///
    /// <para>Sorted because Let's Encrypt's duplicate-certificate limit is counted against the
    /// set, not the order. Reordering the identifiers in the console must not look like a fresh
    /// budget — that would be a way to spend the real limit five times over while the guard
    /// reported everything was fine.</para>
    /// </summary>
    public static string IdentifierKey(IEnumerable<string> identifiers)
        => string.Join(
            ' ',
            identifiers.Select(CertificateStore.Normalize)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(i => i, StringComparer.Ordinal));

    public static async Task<ManagedCertificateBudget> EvaluateAsync(
        EdgeDbContext db,
        EdgeManagedCertificate managed,
        CertificateOptions options,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var key = IdentifierKey(managed.Identifiers);
        var weekAgo = now.AddDays(-7);
        var hourAgo = now.AddHours(-1);

        var recent = await db.ManagedCertificateAttempts.AsNoTracking()
            .Where(a => a.ManagedCertificateId == managed.Id
                        && a.Identifiers == key
                        && a.AttemptedAt >= weekAgo)
            .Select(a => new { a.AttemptedAt, a.Succeeded, a.ReachedCa })
            .ToListAsync(ct);

        var issuances = recent.Where(a => a.Succeeded).ToList();
        var remaining = Math.Max(0, options.ManagedIssuancesPerWeek - issuances.Count);

        if (issuances.Count >= options.ManagedIssuancesPerWeek)
        {
            // The oldest issuance inside the window is what has to age out.
            var retryAfter = issuances.Min(a => a.AttemptedAt).AddDays(7);
            return new ManagedCertificateBudget(
                false,
                $"{issuances.Count} certificate(s) have already been issued for this exact set of "
                + $"identifiers in the last 7 days, and Let's Encrypt allows 5. Not ordering again "
                + $"until {retryAfter:u}. If this is unexpected, something is re-ordering in a loop.",
                retryAfter,
                remaining);
        }

        // ReachedCa: an attempt that never became an order spent none of the CA's
        // failed-authorization budget, so it must not consume this ceiling either. It is recorded
        // anyway, because the operator still needs to see why nothing happened.
        var failures = recent
            .Where(a => !a.Succeeded && a.ReachedCa && a.AttemptedAt >= hourAgo)
            .ToList();
        if (failures.Count >= options.ManagedFailuresPerHour)
        {
            var retryAfter = failures.Min(a => a.AttemptedAt).AddHours(1);
            return new ManagedCertificateBudget(
                false,
                $"{failures.Count} authorization failure(s) for these identifiers in the last hour, "
                + $"and Let's Encrypt allows 5 per hour. Waiting until {retryAfter:u}; the recorded "
                + "cause is on the last attempt.",
                retryAfter,
                remaining);
        }

        return new ManagedCertificateBudget(true, null, null, remaining);
    }

    /// <summary>
    /// Records an attempt.
    ///
    /// <para><paramref name="reachedCa"/> is the whole subtlety: false means the order was never
    /// placed, so the row is history and not budget. Recording it at all is deliberate — the
    /// version of this that recorded nothing produced a console with no expiry, no error and no
    /// attempts, which reads identically to a platform where nobody has pressed anything.</para>
    /// </summary>
    public static async Task RecordAttemptAsync(
        EdgeDbContext db,
        EdgeManagedCertificate managed,
        bool succeeded,
        string? error,
        DateTimeOffset now,
        CancellationToken ct,
        bool reachedCa = true)
    {
        db.ManagedCertificateAttempts.Add(new EdgeManagedCertificateAttempt
        {
            ManagedCertificateId = managed.Id,
            Identifiers = IdentifierKey(managed.Identifiers),
            AttemptedAt = now,
            Succeeded = succeeded,
            ReachedCa = reachedCa,
            Error = error is null ? null : error.Length > 2000 ? error[..2000] : error,
        });
        await db.SaveChangesAsync(ct);
    }
}
