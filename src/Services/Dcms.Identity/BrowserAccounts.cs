using System.Security.Cryptography;
using Dcms.Identity.Data;
using Dcms.Identity.Domain;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Redaction;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Identity;

/// <summary>
/// One platform account that has signed in on one browser, so the sign-in page can offer it
/// again and switch to it without a password while its login is still alive.
///
/// <para><b>Keyed by the browser, not by a cookie per account.</b> The browser carries one
/// random device cookie; the rows live here. A cookie per account would put a full
/// authentication ticket in every request header for each account ever used, and the list
/// could only be read by decrypting all of them. Here the list, the status and "remove from
/// this browser" are a query.</para>
///
/// <para><b>The login id is the session.</b> A row names the <see cref="LoginSessions"/> id its
/// account signed in with. That id is already what every console session carries and what the
/// token endpoint and the edge check, so switching back to an account resumes the same login
/// -- and signing that account out anywhere marks the row signed out here, with nothing to
/// keep in step.</para>
/// </summary>
[AuditIgnore]
public sealed class BrowserAccount
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 of the device cookie. The raw value is a bearer secret for every live
    /// account on that browser, so it is never stored.</summary>
    public string DeviceHash { get; set; } = string.Empty;

    public Guid UserId { get; set; }
    public string LoginSessionId { get; set; } = string.Empty;

    /// <summary>The account's security stamp when it signed in. A password change or a lock
    /// rotates it, which signs the row out the same way it ends the live cookie.</summary>
    public string? SecurityStamp { get; set; }

    public DateTimeOffset LastUsedAt { get; set; }

    /// <summary>Slides with use: every sign-in, authorization and token refresh from this
    /// login pushes it out by <see cref="BrowserAccounts.Lifetime"/>.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>A row as the sign-in page shows it.</summary>
public sealed record BrowserAccountView(Guid Id, string Email, string Name, bool LoggedIn, bool Current);

/// <summary>
/// Reads and writes this browser's accounts. Scoped, because it holds the DbContext.
/// </summary>
public sealed class BrowserAccounts(
    IdentityDbContext db,
    LoginSessionRevocations revocations,
    UserManager<DcmsUser> users,
    AuditScope scope,
    TimeProvider clock)
{
    /// <summary>
    /// The device cookie's name. <c>__Host-</c> over HTTPS, and that prefix is the security
    /// boundary, not decoration: tenant sites on <c>*.dcms.highgeek.eu</c> run their own script
    /// and share the registrable domain, so without it a tenant page could set
    /// <c>dcms.device=&lt;known&gt;; Domain=highgeek.eu</c> — every later sign-in in that browser
    /// would be recorded under a value the attacker holds, and their own browser could then
    /// switch into those accounts with no password. A browser refuses any <c>__Host-</c> cookie
    /// that carries a Domain, so a sibling host cannot plant one. Plain HTTP (development and
    /// the test server) cannot use the prefix, which requires Secure, and has no sibling hosts.
    /// </summary>
    public const string DeviceCookie = "__Host-dcms.device";
    private const string InsecureDeviceCookie = "dcms.device";

    private static string DeviceCookieName(HttpContext http)
        => http.Request.IsHttps ? DeviceCookie : InsecureDeviceCookie;

    /// <summary>
    /// How long a login stays switchable without use. The same 14 days as identity's cookie and
    /// the refresh tokens a login issues, so "Logged in" here means what it means to a console:
    /// a refresh from that login would still succeed.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    /// <summary>
    /// Records that <paramref name="user"/> signed in on this browser, returning the login id
    /// the cookie must carry.
    ///
    /// <para>An id the caller already has (a switch, a re-issued cookie) is kept. Otherwise a
    /// live row for the same account is <i>resumed</i> rather than replaced: signing in again
    /// with a password must not orphan the login the platform console is already renewing
    /// from, which would leave it running on an id nothing here lists.</para>
    /// </summary>
    public async Task<string> RecordAsync(
        HttpContext http, DcmsUser user, string? loginSessionId, CancellationToken ct = default)
    {
        var device = HashOf(EnsureDeviceCookie(http));
        var row = await db.BrowserAccounts
            .SingleOrDefaultAsync(r => r.DeviceHash == device && r.UserId == user.Id, ct);

        if (string.IsNullOrEmpty(loginSessionId))
        {
            loginSessionId = row is not null && await IsLiveAsync(row, user, ct)
                ? row.LoginSessionId
                : LoginSessions.New(user.Id.ToString());
        }

        if (row is null)
        {
            row = new BrowserAccount { Id = Guid.NewGuid(), DeviceHash = device, UserId = user.Id };
            db.BrowserAccounts.Add(row);
        }

        Stamp(row, user, loginSessionId);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // A double-submitted sign-in: the other request inserted this account's row between
            // our read and our write. Take its row instead of failing the sign-in with a 500.
            db.Entry(row).State = EntityState.Detached;
            row = await db.BrowserAccounts.SingleAsync(r => r.DeviceHash == device && r.UserId == user.Id, ct);
            Stamp(row, user, loginSessionId);
            await db.SaveChangesAsync(ct);
        }
        return loginSessionId;
    }

    private void Stamp(BrowserAccount row, DcmsUser user, string loginSessionId)
    {
        var now = clock.GetUtcNow();
        row.LoginSessionId = loginSessionId;
        row.SecurityStamp = user.SecurityStamp;
        row.LastUsedAt = now;
        row.ExpiresAt = now + Lifetime;
    }

    /// <summary>
    /// Pushes a login's expiry out. Called on every authorization and token refresh, so a
    /// console that keeps renewing keeps its account "Logged in" here — the two cannot drift.
    /// One indexed UPDATE, and a no-op for a login no browser row names.
    /// </summary>
    public async Task TouchAsync(string loginSessionId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var expires = now + Lifetime;
        // Suppressed: a renewal every ten minutes per console is mechanics, not an action, and
        // the sign-in that created the row is already on the record.
        using (scope.SuppressBulkCapture())
        {
            await db.BrowserAccounts
                .Where(r => r.LoginSessionId == loginSessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.LastUsedAt, now).SetProperty(r => r.ExpiresAt, expires), ct);
        }
    }

    /// <summary>This browser's accounts, most recently used first. <paramref name="currentLoginSessionId"/>
    /// marks the one identity's cookie is signed in as right now.</summary>
    public async Task<IReadOnlyList<BrowserAccountView>> ListAsync(
        HttpContext http, string? currentLoginSessionId, CancellationToken ct = default)
    {
        if (DeviceHashOf(http) is not { } device)
        {
            return [];
        }

        var rows = await db.BrowserAccounts.AsNoTracking()
            .Where(r => r.DeviceHash == device)
            .OrderByDescending(r => r.LastUsedAt)
            .Join(db.Users, r => r.UserId, u => u.Id, (r, u) => new { Row = r, User = u })
            .ToListAsync(ct);

        var views = new List<BrowserAccountView>(rows.Count);
        foreach (var x in rows)
        {
            views.Add(new BrowserAccountView(
                x.Row.Id,
                x.User.Email ?? string.Empty,
                x.User.DisplayName ?? x.User.UserName ?? x.User.Email ?? string.Empty,
                await IsLiveAsync(x.Row, x.User, ct),
                string.Equals(x.Row.LoginSessionId, currentLoginSessionId, StringComparison.Ordinal)));
        }
        return views;
    }

    /// <summary>
    /// The account and login to switch to, or null unless the row belongs to THIS browser and
    /// its login is alive. The device check is the whole authorisation: a row id from another
    /// browser is a guess, and answers exactly like a row that does not exist.
    /// </summary>
    public async Task<(DcmsUser User, string LoginSessionId)?> FindLiveAsync(
        HttpContext http, Guid id, CancellationToken ct = default)
    {
        if (DeviceHashOf(http) is not { } device
            || await db.BrowserAccounts.SingleOrDefaultAsync(r => r.Id == id && r.DeviceHash == device, ct) is not { } row
            || await users.FindByIdAsync(row.UserId.ToString()) is not { } user
            || !await IsLiveAsync(row, user, ct))
        {
            return null;
        }
        return (user, row.LoginSessionId);
    }

    /// <summary>
    /// Takes an account off this browser — and ends its login first, so "remove" on a shared
    /// computer cannot leave a console somewhere still signed in as the person who just left.
    /// </summary>
    public async Task<bool> ForgetAsync(HttpContext http, Guid id, CancellationToken ct = default)
    {
        if (DeviceHashOf(http) is not { } device
            || await db.BrowserAccounts.SingleOrDefaultAsync(r => r.Id == id && r.DeviceHash == device, ct) is not { } row)
        {
            return false;
        }
        await revocations.RevokeAsync(row.LoginSessionId, ct);
        db.BrowserAccounts.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Whether a login has ended for good: revoked, or its browser row says it is no longer
    /// alive. A login no row names (a cookie from before rows existed) is judged on revocation
    /// alone, which is exactly what it was judged on before.
    /// </summary>
    public async Task<bool> HasEndedAsync(string loginSessionId, CancellationToken ct = default)
    {
        if (await revocations.IsRevokedAsync(loginSessionId, ct))
        {
            return true;
        }
        var row = await db.BrowserAccounts.AsNoTracking()
            .FirstOrDefaultAsync(r => r.LoginSessionId == loginSessionId, ct);
        if (row is null)
        {
            return false;
        }
        return await users.FindByIdAsync(row.UserId.ToString()) is not { } user
               || !await IsLiveAsync(row, user, ct);
    }

    private async Task<bool> IsLiveAsync(BrowserAccount row, DcmsUser user, CancellationToken ct)
        => row.ExpiresAt > clock.GetUtcNow()
           && string.Equals(row.SecurityStamp, user.SecurityStamp, StringComparison.Ordinal)
           && !await users.IsLockedOutAsync(user)
           && !await revocations.IsRevokedAsync(row.LoginSessionId, ct);

    private static string? DeviceHashOf(HttpContext http)
        => http.Request.Cookies.TryGetValue(DeviceCookieName(http), out var value) && !string.IsNullOrEmpty(value)
            ? HashOf(value)
            : null;

    /// <summary>
    /// The device cookie, set if this browser has none. Host-only on the auth host, HttpOnly,
    /// and good for the browser's maximum of 400 days: it names a browser, not a login, and the
    /// rows behind it expire on their own. Only ever a value minted HERE — see
    /// <see cref="DeviceCookie"/> for why the name is what keeps it that way.
    /// </summary>
    private static string EnsureDeviceCookie(HttpContext http)
    {
        var name = DeviceCookieName(http);
        if (http.Request.Cookies.TryGetValue(name, out var existing) && !string.IsNullOrEmpty(existing))
        {
            return existing;
        }
        // Set on a sign-in response; a second sign-in in the same request must reuse it.
        if (http.Items[DeviceCookie] is string minted)
        {
            return minted;
        }

        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        http.Items[DeviceCookie] = value;
        http.Response.Cookies.Append(name, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromDays(400),
            IsEssential = true,
        });
        return value;
    }

    private static string HashOf(string value)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
