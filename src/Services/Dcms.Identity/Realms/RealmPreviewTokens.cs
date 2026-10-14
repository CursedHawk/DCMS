using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Dcms.Identity.Realms;

/// <summary>
/// "View as" in a site builder's preview (#269): a short-lived realm access token for one user,
/// asked for by an administrator who may manage the realm's users — without that user's password.
///
/// <para>It is the token the user's own sign-in would get (same issuer, signing key, audience
/// <c>dcms.realm:{tenantId}</c>, <c>realm</c>, groups), so content-api judges the preview exactly as
/// it would judge them. What sets it apart is that it says so: <c>dcms_preview</c> marks it, and
/// <c>act</c> names the administrator, so nothing reading it can mistake it for the user's own
/// sign-in. There is no refresh token: when it lapses the preview is signed out, and asking again
/// is another audited request.</para>
/// </summary>
public static class RealmPreviewTokens
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public const string PreviewClaim = "dcms_preview";

    public static string Mint(
        OpenIddictServerOptions server, string issuer, RealmUser user, IEnumerable<Guid> groups, Guid requestedBy, DateTimeOffset now)
    {
        var claims = new Dictionary<string, object>
        {
            [Claims.Subject] = user.Id.ToString(),
            [RealmStore.RealmClaim] = user.TenantId.ToString(),
            [Claims.Name] = user.DisplayName ?? user.Email,
            [Claims.Email] = user.Email,
            [RealmStore.GroupsClaim] = groups.Select(g => g.ToString()).ToArray(),
            [PreviewClaim] = true,
            // RFC 8693: the party acting as the subject.
            ["act"] = new Dictionary<string, object> { [Claims.Subject] = requestedBy.ToString() },
            [Claims.JwtId] = Guid.NewGuid().ToString("N"),
        };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = RealmAuthorization.Audience(user.TenantId),
            Claims = claims,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = (now + Lifetime).UtcDateTime,
            SigningCredentials = server.SigningCredentials.First(),
            TokenType = JsonWebTokenTypes.AccessToken,
        });
    }

    /// <summary>The issuer OpenIddict stamps its own tokens with: the configured one, else this request's base address.</summary>
    public static string IssuerOf(OpenIddictServerOptions server, HttpRequest request) =>
        server.Issuer?.AbsoluteUri ?? $"{request.Scheme}://{request.Host}{request.PathBase}/";
}
