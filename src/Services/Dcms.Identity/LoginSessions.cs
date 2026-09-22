using Dcms.Identity.Data;
using Dcms.Shared.Audit;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Identity;

/// <summary>
/// One browser's interactive login at identity, named so it can be ended on its own.
///
/// <para><b>Why this exists.</b> Ending an edge BFF session (ADR 0014) deletes the console's
/// tokens, and the console correctly falls back to its sign-in screen — but identity's own
/// cookie is untouched, so pressing "Sign in" completes <c>/connect/authorize</c> silently and
/// the device is back in within one redirect. "Sign out that laptop" has to reach the login
/// behind the session, not only the session.</para>
///
/// <para>ASP.NET Identity's answer to this is the security stamp, and it is the wrong shape: a
/// rotated stamp ends <i>every</i> device, which is the one thing the account page is not
/// asking for. So each login carries an id, the ids that have been ended are recorded, and the
/// cookie is checked against that record.</para>
///
/// <para><b>Revocations, not sessions.</b> Only ended logins are stored, so the table holds a
/// handful of rows and a live login writes nothing. The cost is that identity cannot enumerate
/// logins — it does not need to: the edge already has the list, because a BFF session is what
/// the account page is showing.</para>
/// </summary>
public static class LoginSessions
{
    /// <summary>
    /// Where the id rides: an authentication-property item on the Identity cookie rather than a
    /// claim.
    ///
    /// <para>Claims are rebuilt from the user every time <c>SecurityStampValidator</c> refreshes
    /// the cookie — roughly every 30 minutes — so a claim would be silently replaced with a new
    /// id and the recorded one would stop naming anything. Properties are carried across that
    /// refresh untouched, which is exactly the lifetime wanted here: the login.</para>
    /// </summary>
    public const string PropertyItem = "dcms_lsid";

    /// <summary>
    /// The claim carrying it into the ID token, and therefore to the edge.
    ///
    /// <para>Deliberately not <c>sid</c>: that name belongs to OIDC session management, which
    /// this is not, and a claim the authorization server also has opinions about is a claim that
    /// can be filtered or rewritten under you.</para>
    /// </summary>
    public const string ClaimType = "dcms_lsid";

    /// <summary>
    /// How long an ended login stays on the revocation list. Must outlast the cookie it refuses,
    /// or a forgotten row lets an expired-but-presented cookie back in — see
    /// <c>Program.cs</c>, where the cookie lifetime is pinned to the same span.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    /// <summary>
    /// A new id for a login: the owner's subject, then randomness.
    ///
    /// <para>The subject prefix is what lets identity check that a caller asking to end a login
    /// owns it, <b>without storing live logins at all</b>. The random half is what stops the
    /// prefix from being the whole id — a caller can name their own subject, so guessing has to
    /// be the hard part.</para>
    /// </summary>
    public static string New(string subject) => $"{subject}.{Guid.NewGuid():N}";

    /// <summary>Who a login id belongs to, or null if it is not one of ours.</summary>
    public static string? SubjectOf(string? id)
    {
        var dot = id?.LastIndexOf('.') ?? -1;
        return dot > 0 ? id![..dot] : null;
    }
}

/// <summary>An interactive login that has been ended and must not authenticate again.</summary>
public sealed class RevokedLoginSession
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset RevokedAt { get; set; }
    /// <summary>When the cookie this refuses can no longer be presented, and the row may go.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Reads and writes the revocation list. Scoped, because it holds the DbContext.</summary>
public sealed class LoginSessionRevocations(IdentityDbContext db, AuditScope scope)
{
    /// <summary>
    /// Asked on every cookie-authenticated request to identity.
    ///
    /// <para>ponytail: one indexed primary-key lookup, uncached. Identity's cookie-authenticated
    /// surface is the account pages and the authorize endpoint — not a request path anything
    /// hot goes through, and every API here is bearer-authenticated instead. Upgrade path if
    /// that changes: cache the (small) revoked set behind a short TTL.</para>
    /// </summary>
    public Task<bool> IsRevokedAsync(string id, CancellationToken ct = default)
        => db.Set<RevokedLoginSession>().AsNoTracking().AnyAsync(r => r.Id == id, ct);

    /// <summary>Ends a login. Idempotent — asking twice is a no-op, not a duplicate key.</summary>
    public async Task RevokeAsync(string id, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Swept here rather than by a background service: this is the only thing that ever adds
        // a row, it runs perhaps a few times a day, and a table that prunes itself on write
        // cannot be the one whose sweeper nobody noticed had stopped.
        //
        // Suppressed rather than recorded: what it deletes is rows whose cookies can no longer
        // be presented, so the statement destroys nothing anybody could act on. The revocation
        // that put them there is recorded by the endpoint (AuditActions.SessionRevoked), which
        // is the record worth having.
        using (scope.SuppressBulkCapture())
        {
            await db.Set<RevokedLoginSession>().Where(r => r.ExpiresAt < now).ExecuteDeleteAsync(ct);
        }

        if (await db.Set<RevokedLoginSession>().AnyAsync(r => r.Id == id, ct))
        {
            return;
        }

        db.Add(new RevokedLoginSession { Id = id, RevokedAt = now, ExpiresAt = now + LoginSessions.Retention });
        await db.SaveChangesAsync(ct);
    }
}
