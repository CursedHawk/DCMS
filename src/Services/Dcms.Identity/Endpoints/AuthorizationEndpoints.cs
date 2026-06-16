using System.Security.Claims;
using Dcms.Identity.Domain;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Dcms.Identity.Endpoints;

/// <summary>
/// OpenIddict authorize / token / userinfo / logout endpoints.
/// Access tokens are signed JWTs (encryption disabled) so resource servers
/// validate them with plain JwtBearer against the discovery document.
/// </summary>
public static class AuthorizationEndpoints
{
    public static IEndpointRouteBuilder MapAuthorizationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/connect/authorize", ["GET", "POST"], AuthorizeAsync);
        app.MapPost("/connect/token", ExchangeAsync);
        app.MapMethods("/connect/userinfo", ["GET", "POST"], UserInfoAsync);
        app.MapMethods("/connect/logout", ["GET", "POST"], LogoutAsync);
        return app;
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpContext context,
        UserManager<DcmsUser> userManager,
        SignInManager<DcmsUser> signInManager,
        IOpenIddictScopeManager scopeManager)
    {
        var request = context.GetOpenIddictServerRequest()
                      ?? throw new InvalidOperationException("OpenIddict request not found.");

        // Require an authenticated cookie session; otherwise bounce to login.
        var result = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!result.Succeeded || result.Principal?.Identity?.IsAuthenticated != true)
        {
            var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = "/account/login?returnUrl=" + Uri.EscapeDataString(returnUrl) },
                [IdentityConstants.ApplicationScheme]);
        }

        var user = await userManager.GetUserAsync(result.Principal)
                   ?? throw new InvalidOperationException("Authenticated user not found.");

        var principal = await BuildUserPrincipalAsync(user, userManager, scopeManager, request.GetScopes());
        return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> ExchangeAsync(
        HttpContext context,
        UserManager<DcmsUser> userManager,
        IOpenIddictScopeManager scopeManager)
    {
        var request = context.GetOpenIddictServerRequest()
                      ?? throw new InvalidOperationException("OpenIddict request not found.");

        if (request.IsClientCredentialsGrantType())
        {
            // Service-to-service: the subject is the client application itself.
            var identity = new ClaimsIdentity(
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                Claims.Name,
                Claims.Role);
            identity.AddClaim(new Claim(Claims.Subject, request.ClientId!).SetDestinations(Destinations.AccessToken));
            identity.AddClaim(new Claim(Claims.Name, request.ClientId!).SetDestinations(Destinations.AccessToken));

            var principal = new ClaimsPrincipal(identity);
            principal.SetScopes(request.GetScopes());
            await SetResourcesAsync(principal, scopeManager);
            return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            var user = result.Principal is null ? null : await userManager.GetUserAsync(result.Principal);
            if (user is null)
            {
                return Results.Forbid(
                    authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme],
                    properties: new AuthenticationProperties(new Dictionary<string, string?>
                    {
                        [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
                        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The token is no longer valid.",
                    }));
            }

            var principal = await BuildUserPrincipalAsync(user, userManager, scopeManager, result.Principal!.GetScopes());
            return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        return Results.BadRequest(new { error = Errors.UnsupportedGrantType });
    }

    private static async Task<IResult> UserInfoAsync(HttpContext context, UserManager<DcmsUser> userManager)
    {
        var principal = (await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
        if (principal is null)
        {
            return Results.Unauthorized();
        }

        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(new Dictionary<string, object?>
        {
            [Claims.Subject] = user.Id.ToString(),
            [Claims.Email] = user.Email,
            [Claims.Name] = user.DisplayName ?? user.UserName,
            ["roles"] = await userManager.GetRolesAsync(user),
        });
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, SignInManager<DcmsUser> signInManager)
    {
        await signInManager.SignOutAsync();
        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }

    /// <summary>
    /// Builds the principal whose claims become the tokens. Granted scopes drive
    /// the access-token audiences (resources). Permission claims are deliberately
    /// absent — resource servers resolve permissions per request (Phase 3).
    /// </summary>
    private static async Task<ClaimsPrincipal> BuildUserPrincipalAsync(
        DcmsUser user, UserManager<DcmsUser> userManager,
        IOpenIddictScopeManager scopeManager, IEnumerable<string> scopes)
    {
        var identity = new ClaimsIdentity(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            Claims.Name,
            Claims.Role);

        identity.AddClaim(new Claim(Claims.Subject, user.Id.ToString())
            .SetDestinations(Destinations.AccessToken, Destinations.IdentityToken));
        identity.AddClaim(new Claim(Claims.Name, user.DisplayName ?? user.UserName ?? string.Empty)
            .SetDestinations(Destinations.AccessToken, Destinations.IdentityToken));
        if (!string.IsNullOrEmpty(user.Email))
        {
            identity.AddClaim(new Claim(Claims.Email, user.Email)
                .SetDestinations(Destinations.AccessToken, Destinations.IdentityToken));
        }

        foreach (var role in await userManager.GetRolesAsync(user))
        {
            identity.AddClaim(new Claim(Claims.Role, role)
                .SetDestinations(Destinations.AccessToken, Destinations.IdentityToken));
        }

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes);
        await SetResourcesAsync(principal, scopeManager);
        return principal;
    }

    /// <summary>
    /// Stamps the access-token audiences (<c>aud</c>) from the granted scopes'
    /// registered resources. Without this the issued JWT carries no audience and
    /// resource servers (audience-validating JwtBearer) reject it with 401.
    /// </summary>
    private static async Task SetResourcesAsync(ClaimsPrincipal principal, IOpenIddictScopeManager scopeManager)
    {
        var resources = new List<string>();
        await foreach (var resource in scopeManager.ListResourcesAsync(principal.GetScopes()))
        {
            resources.Add(resource);
        }
        principal.SetResources(resources);
    }
}
