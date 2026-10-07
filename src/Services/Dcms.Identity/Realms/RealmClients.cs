using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Dcms.Identity.Realms;

/// <summary>
/// The OIDC client the edge signs a tenant's site users in with: one per realm,
/// <c>site:{tenantId}</c>, confidential and PKCE, with a redirect URI per site hostname.
///
/// <para><b>The secret is derived, not stored.</b> It is HMAC-SHA256 of the client id under one
/// master secret (<c>Identity:EdgeSites:Secret</c>) that only identity and the edge hold. The
/// edge can therefore authenticate as any realm's client without a per-tenant secret store, and
/// rotating the master rotates every one of them at once. A client id alone is worthless.</para>
///
/// <para><b>One client per tenant</b>, not one shared client with every tenant's hostnames:
/// OpenIddict compares redirect URIs exactly per client, so a code minted for tenant A's client
/// can only ever be delivered to tenant A's hosts, and the client id names the realm the
/// authorization is for.</para>
/// </summary>
public static class RealmClients
{
    public const string Prefix = "site:";
    public const string SecretSetting = "Identity:EdgeSites:Secret";

    /// <summary>
    /// The edge's site sign-in callback and sign-out landing, on every tenant host it gates.
    /// Not the platform's <c>/.edge/signin-oidc</c>: both handlers look at every request, and two
    /// claiming one path means the wrong one tries — and fails — to read the other's state.
    /// </summary>
    public const string CallbackPath = "/.edge/site/signin-oidc";
    public const string SignedOutPath = "/.edge/site/signout-callback-oidc";

    public static string IdFor(Guid tenantId) => Prefix + tenantId.ToString("N");

    public static bool TryParse(string? clientId, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        return clientId is not null
               && clientId.StartsWith(Prefix, StringComparison.Ordinal)
               && Guid.TryParseExact(clientId[Prefix.Length..], "N", out tenantId);
    }

    public static string SecretFor(string masterSecret, string clientId) =>
        WebEncoders.Base64UrlEncode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(masterSecret), Encoding.UTF8.GetBytes(clientId)));

    /// <summary>Creates or converges the realm's client. False when no master secret is configured: no client, no sign-in.</summary>
    public static async Task<bool> EnsureAsync(IOpenIddictApplicationManager manager, IConfiguration configuration, Realm realm, CancellationToken ct)
    {
        var master = configuration[SecretSetting];
        if (string.IsNullOrWhiteSpace(master))
        {
            return false;
        }
        var clientId = IdFor(realm.TenantId);
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = SecretFor(master, clientId),
            ClientType = ClientTypes.Confidential,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = $"{realm.Name} (sites)",
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.EndSession,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Email,
                Permissions.Scopes.Profile,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };
        foreach (var host in realm.Hosts)
        {
            descriptor.RedirectUris.Add(new Uri($"https://{host}{CallbackPath}"));
            descriptor.PostLogoutRedirectUris.Add(new Uri($"https://{host}{SignedOutPath}"));
        }

        if (await manager.FindByClientIdAsync(clientId, ct) is { } existing)
        {
            await manager.UpdateAsync(existing, descriptor, ct);
        }
        else
        {
            await manager.CreateAsync(descriptor, ct);
        }
        return true;
    }

    public static async Task DeleteAsync(IOpenIddictApplicationManager manager, Guid tenantId, CancellationToken ct)
    {
        if (await manager.FindByClientIdAsync(IdFor(tenantId), ct) is { } existing)
        {
            await manager.DeleteAsync(existing, ct);
        }
    }
}
