using System.Security.Claims;
using System.Text.Json;
using Dcms.Identity.Data;
using Dcms.Identity.Domain;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using static Dcms.Identity.Endpoints.AccountEndpoints;

namespace Dcms.Identity.Realms;

/// <summary>
/// Who a provider says someone is, reduced to what a realm decides on.
/// </summary>
/// <param name="Key">The provider's stable subject: Google <c>sub</c>, Entra <c>tid:oid</c>, OIDC <c>iss|sub</c>, DCMS the platform user id.</param>
/// <param name="EmailTrusted">Whether the provider vouches the email is this person's — the only condition under which an email may find, link or create an account.</param>
public sealed record ExternalIdentity(string Key, string? Email, bool EmailTrusted, string? Name, IReadOnlyList<string> Groups);

/// <summary>
/// Signing in through a provider (ADR 0022, UA2). An existing link wins; else a trusted email
/// finds the invited or existing account and links it; else, if the provider's policy allows
/// that email's domain, an account is created. Anyone else is turned away — there is no
/// self-service sign-up into a tenant.
/// </summary>
public static class RealmSso
{
    public static ExternalIdentity FromOidc(RealmProvider provider, ClaimsPrincipal principal)
    {
        string? Claim(string type) => principal.FindFirst(type)?.Value;
        var email = Claim("email");
        var verified = string.Equals(Claim("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
        var (key, trusted) = provider.Kind switch
        {
            // Google says whether it verified the address, and the hd claim was already checked.
            RealmProviderKind.Google => (Claim("sub"), verified),
            // Entra's email is an editable directory attribute, not a verified address: anyone
            // able to set a user's mail could otherwise claim another person's account ("nOAuth").
            // Trusted only with xms_edov — Microsoft's "the email's domain is verified by this
            // directory" optional claim, which the tenant adds to its app registration. Without
            // it, an Entra account gets in through an invitation link or an existing link only.
            // preferred_username (a UPN) is never an email.
            RealmProviderKind.Entra => (Claim("tid") is { } tid && Claim("oid") is { } oid ? $"{tid}:{oid}" : null,
                Claim("xms_edov") is "true" or "1" or "True"),
            // The issuer as configured: the handler already held the token's iss to it.
            _ => (Claim("sub") is { } sub ? $"{provider.Issuer}|{sub}" : null, verified),
        };
        return new ExternalIdentity(
            key ?? throw new InvalidOperationException("The provider did not name the account."),
            email, trusted && email is not null, Claim("name"), GroupValues(principal, provider.GroupClaim));
    }

    private static List<string> GroupValues(ClaimsPrincipal principal, string? claim)
    {
        if (claim is null)
        {
            return [];
        }
        var values = new List<string>();
        foreach (var value in principal.FindAll(claim).Select(c => c.Value))
        {
            // Some providers send the list as one JSON array claim.
            if (value.StartsWith('[') && JsonDocument.Parse(value).RootElement is { ValueKind: JsonValueKind.Array } array)
            {
                values.AddRange(array.EnumerateArray().Select(e => e.ToString()));
            }
            else
            {
                values.Add(value);
            }
        }
        return values;
    }

    /// <summary>The realm account this identity signs in as, linking or creating it per the provider's policy; null when there is none for them.</summary>
    public static async Task<RealmUser?> ResolveAsync(IdentityDbContext db, RealmStore store, RealmProvider provider, ExternalIdentity external,
        RealmUser? invited, CancellationToken ct)
    {
        var tenantId = provider.TenantId;
        var activated = false;
        var login = await db.RealmLogins.FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Provider == provider.Key && l.ProviderKey == external.Key, ct);
        RealmUser? user = login is null ? null : await store.FindUserAsync(tenantId, login.UserId, ct);

        if (user is null)
        {
            // The invitation link itself vouches for the account it names, whatever the email.
            user = invited
                   ?? (external.EmailTrusted ? await store.FindByEmailAsync(tenantId, external.Email!, ct) : null);
            if (user is null && external.EmailTrusted && provider.Provisioning == RealmProvisioning.AllowedDomains
                && DomainOf(external.Email!) is { } domain && provider.AllowedDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
            {
                user = new RealmUser
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    Email = external.Email!,
                    NormalizedEmail = RealmStore.Normalize(external.Email!),
                    DisplayName = external.Name,
                    Status = RealmUserStatus.Active,
                };
                db.RealmUsers.Add(user);
                activated = true;
                var existing = await db.RealmGroups.Where(g => g.TenantId == tenantId && provider.DefaultGroups.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
                db.RealmGroupMembers.AddRange(existing.Select(g => new RealmGroupMember { TenantId = tenantId, GroupId = g, UserId = user.Id }));
            }
            if (user is null || user.Status == RealmUserStatus.Disabled)
            {
                return null;
            }
            db.RealmLogins.Add(new RealmLogin { Id = Guid.NewGuid(), TenantId = tenantId, UserId = user.Id, Provider = provider.Key, ProviderKey = external.Key });
            if (user.Status == RealmUserStatus.Invited)
            {
                // Signing in through a trusted provider accepts the invitation; the stamp moves
                // on so the emailed link stops working.
                user.Status = RealmUserStatus.Active;
                RealmStore.Restamp(user);
                activated = true;
            }
        }
        if (!store.CanSignIn(user))
        {
            return null;
        }

        await SyncGroupsAsync(db, provider, user, external.Groups, ct);
        user.LastSignInAt = DateTimeOffset.UtcNow;
        if (user.DisplayName is null && external.Name is { Length: > 0 and <= 200 } name)
        {
            user.DisplayName = name;
        }
        await db.SaveChangesAsync(ct);
        if (activated)
        {
            await store.ActivatedAsync(user, ct);
        }
        return user;
    }

    /// <summary>
    /// The provider's word on groups it maps: a mapped realm group is joined when the person is in
    /// the provider group and left when they are not. Groups no mapping names are the realm's to manage.
    /// </summary>
    private static async Task SyncGroupsAsync(IdentityDbContext db, RealmProvider provider, RealmUser user, IReadOnlyList<string> groups, CancellationToken ct)
    {
        if (provider.GroupMappings.Count == 0)
        {
            return;
        }
        var managed = provider.GroupMappings.Values.ToHashSet();
        var wanted = provider.GroupMappings.Where(m => groups.Contains(m.Key, StringComparer.Ordinal)).Select(m => m.Value).ToHashSet();
        var current = await db.RealmGroupMembers.Where(m => m.TenantId == user.TenantId && m.UserId == user.Id && managed.Contains(m.GroupId)).ToListAsync(ct);
        db.RealmGroupMembers.RemoveRange(current.Where(m => !wanted.Contains(m.GroupId)));
        var existing = await db.RealmGroups.Where(g => g.TenantId == user.TenantId && wanted.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
        foreach (var group in existing.Where(g => current.All(m => m.GroupId != g)))
        {
            db.RealmGroupMembers.Add(new RealmGroupMember { TenantId = user.TenantId, GroupId = group, UserId = user.Id });
        }
    }

    private static string? DomainOf(string email) => email.LastIndexOf('@') is var at and > 0 ? email[(at + 1)..].ToLowerInvariant() : null;

    // ---------------------------------------------------------------------------------- endpoints

    public static IEndpointRouteBuilder MapRealmSsoEndpoints(this IEndpointRouteBuilder app)
    {
        var realm = app.MapGroup("/realm/{slug}/sso/{key}").AllowAnonymous();

        // Out to the provider. An invitation token comes along when someone accepts an
        // invitation through a provider, so the account it names is the one linked.
        realm.MapGet("", async (string slug, string key, string? returnUrl, string? invite, HttpContext http, IdentityDbContext db,
            RealmStore store, RealmOidcSchemes schemes, CancellationToken ct) =>
        {
            if (await ProviderAsync(db, store, slug, key, ct) is not { } found)
            {
                return Results.Redirect($"/realm/{Uri.EscapeDataString(slug)}/login");
            }
            var (r, provider) = found;
            var complete = $"/realm/{Uri.EscapeDataString(slug)}/sso/{Uri.EscapeDataString(key)}/complete"
                           + $"?returnUrl={Uri.EscapeDataString(returnUrl ?? "")}&invite={Uri.EscapeDataString(invite ?? "")}";
            if (provider.Kind == RealmProviderKind.Dcms)
            {
                // The platform's own sign-in, then back here: no OIDC round trip to ourselves. The
                // completion links accounts, so it must be this browser's own round trip and not a
                // link someone planted: a random value in a cookie only this browser holds, echoed
                // in the URL.
                var state = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
                http.Response.Cookies.Append(LinkCookie, state, new CookieOptions
                {
                    HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = http.Request.IsHttps,
                    Path = "/realm/", MaxAge = TimeSpan.FromMinutes(15),
                });
                return Results.Redirect("/account/login?returnUrl=" + Uri.EscapeDataString($"{complete}&state={state}"));
            }
            var properties = new AuthenticationProperties { RedirectUri = complete };
            properties.Items[RealmExternal.ProviderItem] = provider.Id.ToString();
            properties.Items["dcms.realm.slug"] = r.Slug;
            return Results.Challenge(properties, [await schemes.EnsureAsync(provider, ct)]);
        });

        realm.MapGet("/complete", async (string slug, string key, string? returnUrl, string? invite, string? state, HttpContext http, IdentityDbContext db,
            RealmStore store, UserManager<DcmsUser> platformUsers, IAuditRecorder audit, CancellationToken ct) =>
        {
            if (await ProviderAsync(db, store, slug, key, ct) is not { } found)
            {
                return Results.Redirect($"/realm/{Uri.EscapeDataString(slug)}/login");
            }
            var (r, provider) = found;
            ExternalIdentity? external = null;
            if (provider.Kind == RealmProviderKind.Dcms)
            {
                // The platform cookie is read here and nowhere else in a realm: to learn which
                // platform account is being linked, never to sign anyone in to the realm with it.
                var started = http.Request.Cookies[LinkCookie];
                http.Response.Cookies.Delete(LinkCookie, new CookieOptions { Path = "/realm/" });
                var platform = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
                if (!string.IsNullOrEmpty(started) && string.Equals(started, state, StringComparison.Ordinal)
                    && platform.Succeeded && await platformUsers.GetUserAsync(platform.Principal!) is { } member)
                {
                    external = new ExternalIdentity(member.Id.ToString(), member.Email, member.EmailConfirmed && member.Email is not null,
                        member.DisplayName, []);
                }
            }
            else
            {
                var result = await http.AuthenticateAsync(RealmExternal.Scheme);
                await http.SignOutAsync(RealmExternal.Scheme);
                if (result.Succeeded && result.Properties?.Items.TryGetValue(RealmExternal.ProviderItem, out var challenged) == true && challenged == provider.Id.ToString())
                {
                    external = FromOidc(provider, result.Principal!);
                }
            }
            if (external is null)
            {
                return Results.Redirect($"/realm/{Uri.EscapeDataString(slug)}/login?error=sso");
            }

            var invited = string.IsNullOrEmpty(invite) ? null : await store.FromInviteAsync(r.TenantId, invite, ct);
            var user = await ResolveAsync(db, store, provider, external, invited, ct);
            if (user is null)
            {
                audit.Declare(AuditActions.RealmLoginFailed).InTenant(r.TenantId).As(AuditCategory.Auth, AuditSeverity.Notice)
                    .With("method", provider.Key).With("email", external.Email).Failed("no-account");
                return Results.Content(Layout($"{r.Name} — No account", $$"""
                    <form>
                      <h1>No account here</h1>
                      <p class="lead">{{Enc(external.Email ?? "This account")}} has no access to {{Enc(r.Name)}}. Ask an administrator for an invitation.</p>
                      <p class="alt"><a href="/realm/{{Uri.EscapeDataString(r.Slug)}}/login">Back to sign in</a></p>
                    </form>
                    """), "text/html", statusCode: StatusCodes.Status403Forbidden);
            }

            await RealmCookies.SignInAsync(http, r.TenantId, RealmStore.CookiePrincipal(user));
            audit.Declare(AuditActions.RealmLoginSucceeded).InTenant(r.TenantId).As(AuditCategory.Auth)
                .About(user.Id).With("method", provider.Key);
            var target = SafeReturnUrl(returnUrl);
            return Results.Redirect(target.StartsWith("/connect/authorize?", StringComparison.Ordinal) ? target : $"/realm/{Uri.EscapeDataString(slug)}/");
        }).AuditExempt("Records its own outcome (realm.login.succeeded / failed) with the provider as the method.");

        return app;
    }

    private const string LinkCookie = "dcms.realm.link";

    private static async Task<(Realm Realm, RealmProvider Provider)?> ProviderAsync(IdentityDbContext db, RealmStore store, string slug, string key, CancellationToken ct)
    {
        if (await store.FindRealmBySlugAsync(slug, ct) is not { } realm)
        {
            return null;
        }
        var provider = await db.RealmProviders.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == realm.TenantId && p.Key == key && p.Enabled, ct);
        return provider is null ? null : (realm, provider);
    }
}
