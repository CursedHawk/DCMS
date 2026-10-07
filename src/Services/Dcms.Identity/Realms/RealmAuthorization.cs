using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Dcms.Identity.Realms;

/// <summary>
/// The OIDC endpoints' realm half (ADR 0022). A <c>site:{tenantId}</c> client is a realm's: its
/// authorization reads that realm's cookie and nothing else, and the tokens it gets name the realm
/// (<c>realm</c>) and are only for it (<c>aud = dcms.realm:{tenantId}</c>). Platform clients never
/// reach here, and a realm client never reaches the platform half — so a platform sign-in cannot
/// satisfy a realm, nor a realm sign-in the console.
/// </summary>
public static class RealmAuthorization
{
    public static string Audience(Guid tenantId) => $"dcms.realm:{tenantId}";

    public static async Task<IResult> AuthorizeAsync(HttpContext context, OpenIddictRequest request, Guid tenantId)
    {
        var store = context.RequestServices.GetRequiredService<RealmStore>();
        var ct = context.RequestAborted;
        if (await store.FindRealmAsync(tenantId, ct) is not { } realm)
        {
            return Forbid(Errors.InvalidClient, "This site has no sign-in.");
        }

        var session = await RealmCookies.AuthenticateAsync(context, tenantId);
        RealmUser? user = null;
        if (session.Succeeded && Guid.TryParse(session.Principal!.FindFirstValue(Claims.Subject), out var userId))
        {
            user = await store.FindUserAsync(tenantId, userId, ct);
        }
        if (user is null || !store.CanSignIn(user))
        {
            // Straight to the realm's form with the whole authorization as the way back; see the
            // platform half for why this is a redirect and not a challenge.
            var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
            return Results.Redirect($"/realm/{Uri.EscapeDataString(realm.Slug)}/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        var principal = await PrincipalAsync(store, user, request.GetScopes(), ct);
        return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// A code or refresh token being exchanged: still the same account, still allowed in, not
    /// restamped since (password reset, disabled, every session ended). Groups are read afresh,
    /// so a membership change reaches the next token rather than waiting for a new sign-in.
    /// </summary>
    public static async Task<IResult> ExchangeAsync(HttpContext context, ClaimsPrincipal minted)
    {
        var store = context.RequestServices.GetRequiredService<RealmStore>();
        var ct = context.RequestAborted;
        if (!Guid.TryParse(minted.FindFirstValue(RealmStore.RealmClaim), out var tenantId)
            || !Guid.TryParse(minted.FindFirstValue(Claims.Subject), out var userId)
            || await store.FindUserAsync(tenantId, userId, ct) is not { } user
            || !store.CanSignIn(user)
            || user.SecurityStamp != minted.FindFirstValue(RealmStore.StampClaim))
        {
            return Forbid(Errors.InvalidGrant, "This sign-in has ended.");
        }
        return Results.SignIn(await PrincipalAsync(store, user, minted.GetScopes(), ct), properties: null,
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    public static async Task<IResult> UserInfoAsync(HttpContext context, ClaimsPrincipal principal)
    {
        var store = context.RequestServices.GetRequiredService<RealmStore>();
        if (!Guid.TryParse(principal.FindFirstValue(RealmStore.RealmClaim), out var tenantId)
            || !Guid.TryParse(principal.FindFirstValue(Claims.Subject), out var userId)
            || await store.FindUserAsync(tenantId, userId, context.RequestAborted) is not { } user)
        {
            return Results.Unauthorized();
        }
        return Results.Ok(new Dictionary<string, object?>
        {
            [Claims.Subject] = user.Id.ToString(),
            [RealmStore.RealmClaim] = tenantId.ToString(),
            [Claims.Email] = user.Email,
            [Claims.Name] = user.DisplayName ?? user.Email,
            [RealmStore.GroupsClaim] = (await store.GroupIdsAsync(user, context.RequestAborted)).Select(g => g.ToString()).ToList(),
        });
    }

    /// <summary>Ends the realm sign-in in this browser; the edge sends its users here to sign out.</summary>
    public static async Task<IResult> LogoutAsync(HttpContext context, Guid tenantId)
    {
        await RealmCookies.SignOutAsync(context, tenantId);
        return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }

    private static async Task<ClaimsPrincipal> PrincipalAsync(RealmStore store, RealmUser user, IEnumerable<string> scopes, CancellationToken ct)
    {
        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
        static Claim Both(string type, string value) => new Claim(type, value).SetDestinations(Destinations.AccessToken, Destinations.IdentityToken);

        identity.AddClaim(Both(Claims.Subject, user.Id.ToString()));
        identity.AddClaim(Both(RealmStore.RealmClaim, user.TenantId.ToString()));
        identity.AddClaim(Both(Claims.Name, user.DisplayName ?? user.Email));
        identity.AddClaim(Both(Claims.Email, user.Email));
        foreach (var group in await store.GroupIdsAsync(user, ct))
        {
            identity.AddClaim(Both(RealmStore.GroupsClaim, group.ToString()));
        }
        // No destination: kept in the authorization code and refresh token, which is all the
        // exchange needs to notice a restamped account, and never in a token anyone else reads.
        identity.AddClaim(new Claim(RealmStore.StampClaim, user.SecurityStamp));

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes);
        principal.SetResources(Audience(user.TenantId));
        return principal;
    }

    private static IResult Forbid(string error, string description) => Results.Forbid(
        authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme],
        properties: new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }));
}
