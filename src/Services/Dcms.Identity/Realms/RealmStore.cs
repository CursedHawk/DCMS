using System.Security.Claims;
using Dcms.Identity.Data;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Messaging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Dcms.Identity.Realms;

/// <summary>
/// The realm sign-in cookie: one scheme, but a cookie <b>per realm</b>
/// (<c>dcms.realm.{tenantId}</c>), named by <see cref="RealmCookieManager"/> from the realm the
/// request is for. A browser signed in to tenant A's realm therefore holds nothing tenant B's
/// realm can even read, and a platform sign-in (identity's own cookie) is a different scheme.
/// </summary>
public static class RealmCookies
{
    public const string Scheme = "DcmsRealm";
    public const string BaseName = "dcms.realm";

    private const string ItemKey = "dcms.realm.tenant";

    /// <summary>Points the cookie scheme at a realm for the rest of this request.</summary>
    public static void Use(HttpContext http, Guid tenantId) => http.Items[ItemKey] = tenantId;

    public static Guid? Current(HttpContext http) =>
        http.Items.TryGetValue(ItemKey, out var value) && value is Guid tenantId ? tenantId : null;

    public static Task SignInAsync(HttpContext http, Guid tenantId, ClaimsPrincipal principal)
    {
        Use(http, tenantId);
        return http.SignInAsync(Scheme, principal);
    }

    public static Task SignOutAsync(HttpContext http, Guid tenantId)
    {
        Use(http, tenantId);
        return http.SignOutAsync(Scheme);
    }

    public static Task<AuthenticateResult> AuthenticateAsync(HttpContext http, Guid tenantId)
    {
        Use(http, tenantId);
        return http.AuthenticateAsync(Scheme);
    }
}

/// <summary>Names the realm cookie after the realm the request is for; with no realm chosen, there is no cookie to read or write.</summary>
public sealed class RealmCookieManager : ICookieManager
{
    private readonly ChunkingCookieManager _inner = new();

    public string? GetRequestCookie(HttpContext context, string key) =>
        RealmCookies.Current(context) is { } tenantId ? _inner.GetRequestCookie(context, Name(key, tenantId)) : null;

    public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options) =>
        _inner.AppendResponseCookie(context, Name(key, Required(context)), value, options);

    public void DeleteCookie(HttpContext context, string key, CookieOptions options) =>
        _inner.DeleteCookie(context, Name(key, Required(context)), options);

    private static string Name(string key, Guid tenantId) => $"{key}.{tenantId:N}";

    private static Guid Required(HttpContext context) => RealmCookies.Current(context)
        ?? throw new InvalidOperationException("A realm cookie was written without choosing the realm (RealmCookies.Use).");
}

public enum RealmSignIn
{
    Succeeded,
    Failed,
    LockedOut,
}

/// <summary>
/// Everything identity does with a realm's users, always within one tenant: lookups, password
/// sign-in with lockout, invitation and reset links, and the principals cookies and tokens are
/// minted from. The one place realm rows are read, so no query forgets its tenant.
/// </summary>
public sealed class RealmStore(IdentityDbContext db, IPasswordHasher<RealmUser> hasher, IDataProtectionProvider protection, TimeProvider clock,
    IEventPublisher events, ILogger<RealmStore> logger)
{
    public const string RealmClaim = "realm";
    public const string GroupsClaim = "groups";

    /// <summary>The security stamp the cookie or token was minted under; never leaves identity.</summary>
    public const string StampClaim = "realm_stamp";

    public const int MinPasswordLength = 10;
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(2);

    private const string InvitePurpose = "dcms.realm.invite";
    private const string ResetPurpose = "dcms.realm.reset";

    public Task<Realm?> FindRealmAsync(Guid tenantId, CancellationToken ct) =>
        db.Realms.FirstOrDefaultAsync(r => r.TenantId == tenantId, ct);

    public Task<Realm?> FindRealmBySlugAsync(string slug, CancellationToken ct) =>
        db.Realms.FirstOrDefaultAsync(r => r.Slug == slug, ct);

    public Task<RealmUser?> FindUserAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
        db.RealmUsers.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Id == userId, ct);

    public Task<RealmUser?> FindByEmailAsync(Guid tenantId, string email, CancellationToken ct)
    {
        var normalized = Normalize(email);
        return db.RealmUsers.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.NormalizedEmail == normalized, ct);
    }

    public Task<List<Guid>> GroupIdsAsync(RealmUser user, CancellationToken ct) =>
        db.RealmGroupMembers.Where(m => m.TenantId == user.TenantId && m.UserId == user.Id).Select(m => m.GroupId).ToListAsync(ct);

    public static string Normalize(string email) => email.Trim().ToUpperInvariant();

    public bool IsLockedOut(RealmUser user) => user.LockoutEnd is { } end && end > clock.GetUtcNow();

    public bool CanSignIn(RealmUser user) => user.Status == RealmUserStatus.Active && !IsLockedOut(user);

    /// <summary>
    /// Checks a password, counting failures towards a lockout. Unknown, invited and disabled
    /// accounts fail exactly like a wrong password, so the form says nothing about who exists.
    /// </summary>
    public async Task<(RealmSignIn Result, RealmUser? User)> PasswordSignInAsync(Guid tenantId, string email, string password, CancellationToken ct)
    {
        var user = await FindByEmailAsync(tenantId, email, ct);
        if (user is null)
        {
            // The same work as a real check, so timing does not tell "no such account" apart.
            hasher.HashPassword(new RealmUser(), password);
            return (RealmSignIn.Failed, null);
        }
        if (IsLockedOut(user))
        {
            return (RealmSignIn.LockedOut, user);
        }
        bool verified;
        if (user.PasswordHash is { } hash)
        {
            verified = hasher.VerifyHashedPassword(user, hash, password) != PasswordVerificationResult.Failed;
        }
        else
        {
            // A provider-only account: the same work again, so timing does not reveal it exists.
            hasher.HashPassword(user, password);
            verified = false;
        }
        if (!verified || user.Status != RealmUserStatus.Active)
        {
            if (++user.AccessFailedCount >= MaxFailedAttempts)
            {
                user.AccessFailedCount = 0;
                user.LockoutEnd = clock.GetUtcNow().Add(LockoutDuration);
                await db.SaveChangesAsync(ct);
                return (RealmSignIn.LockedOut, user);
            }
            await db.SaveChangesAsync(ct);
            return (RealmSignIn.Failed, user);
        }
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.LastSignInAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return (RealmSignIn.Succeeded, user);
    }

    /// <exception cref="ArgumentException">The password is too short.</exception>
    public async Task SetPasswordAsync(RealmUser user, string password, CancellationToken ct)
    {
        if (password.Length < MinPasswordLength)
        {
            throw new ArgumentException($"Use at least {MinPasswordLength} characters.");
        }
        user.PasswordHash = hasher.HashPassword(user, password);
        var accepted = user.Status == RealmUserStatus.Invited;
        if (accepted)
        {
            user.Status = RealmUserStatus.Active;
        }
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        Restamp(user);
        await db.SaveChangesAsync(ct);
        if (accepted)
        {
            await ActivatedAsync(user, ct);
        }
    }

    /// <summary>
    /// Tells the tenant that an account became active (ADR 0022), after it is saved. Best effort:
    /// the account is active either way, and a broker that cannot take the event costs a flow
    /// that would have run on it, not the sign-in.
    /// </summary>
    public async Task ActivatedAsync(RealmUser user, CancellationToken ct)
    {
        try
        {
            await events.PublishAsync(Subjects.RealmUserActivated,
                new RealmUserActivated(Guid.NewGuid(), clock.GetUtcNow(), user.TenantId, user.Id, user.Email), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not announce that realm user {UserId} became active.", user.Id);
        }
    }

    /// <summary>Every cookie, refresh token and emailed link minted for the user so far stops working.</summary>
    public static void Restamp(RealmUser user) => user.SecurityStamp = Guid.NewGuid().ToString("N");

    public string InviteToken(RealmUser user) => Protect(InvitePurpose, user, InviteLifetime);

    public string ResetToken(RealmUser user) => Protect(ResetPurpose, user, ResetLifetime);

    /// <summary>The user an invitation link is for, while it is unexpired and the account has not changed since.</summary>
    public Task<RealmUser?> FromInviteAsync(Guid tenantId, string? token, CancellationToken ct) => UnprotectAsync(InvitePurpose, tenantId, token, ct);

    public Task<RealmUser?> FromResetAsync(Guid tenantId, string? token, CancellationToken ct) => UnprotectAsync(ResetPurpose, tenantId, token, ct);

    private string Protect(string purpose, RealmUser user, TimeSpan lifetime) =>
        protection.CreateProtector(purpose).ToTimeLimitedDataProtector()
            .Protect($"{user.TenantId:N}|{user.Id:N}|{user.SecurityStamp}", lifetime);

    private async Task<RealmUser?> UnprotectAsync(string purpose, Guid tenantId, string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }
        string payload;
        try
        {
            payload = protection.CreateProtector(purpose).ToTimeLimitedDataProtector().Unprotect(token);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
        var parts = payload.Split('|');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out var tenant) || tenant != tenantId
            || !Guid.TryParseExact(parts[1], "N", out var userId))
        {
            return null;
        }
        var user = await FindUserAsync(tenantId, userId, ct);
        // The stamp makes every link single-use: setting the password restamps the account.
        return user is not null && user.SecurityStamp == parts[2] && user.Status != RealmUserStatus.Disabled ? user : null;
    }

    /// <summary>What the realm cookie carries: who, in which realm, under which stamp.</summary>
    public static ClaimsPrincipal CookiePrincipal(RealmUser user)
    {
        var identity = new ClaimsIdentity(RealmCookies.Scheme, Claims.Name, Claims.Role);
        identity.AddClaim(new Claim(Claims.Subject, user.Id.ToString()));
        identity.AddClaim(new Claim(RealmClaim, user.TenantId.ToString()));
        identity.AddClaim(new Claim(StampClaim, user.SecurityStamp));
        identity.AddClaim(new Claim(Claims.Name, user.DisplayName ?? user.Email));
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Every request that reads a realm cookie re-checks it against the account: a disabled,
    /// locked or restamped account (password reset, "sign out everywhere") ends the session here.
    /// </summary>
    public static async Task ValidateCookieAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var store = context.HttpContext.RequestServices.GetRequiredService<RealmStore>();
        var realm = RealmCookies.Current(context.HttpContext);
        if (principal is null || realm is null
            || !Guid.TryParse(principal.FindFirstValue(RealmClaim), out var tenantId) || tenantId != realm
            || !Guid.TryParse(principal.FindFirstValue(Claims.Subject), out var userId)
            || await store.FindUserAsync(tenantId, userId, context.HttpContext.RequestAborted) is not { } user
            || !store.CanSignIn(user)
            || user.SecurityStamp != principal.FindFirstValue(StampClaim))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(RealmCookies.Scheme);
        }
    }
}
