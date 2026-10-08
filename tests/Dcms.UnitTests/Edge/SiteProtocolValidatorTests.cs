using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dcms.Edge.Auth;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// The site client is per host, so the handler's ClientId is a placeholder: the id_token's azp is
/// held to its own audience instead. Against the placeholder every sign-in failed, and the failure
/// redirect looped through identity until the rate limiter stopped it.
/// </summary>
public class SiteProtocolValidatorTests
{
    [Fact]
    public void A_sites_own_token_passes() =>
        Validate("site:abc", azp: "site:abc").Should().NotThrow();

    [Fact]
    public void A_token_handed_to_another_client_does_not() =>
        Validate("site:abc", azp: "site:other").Should().Throw<OpenIdConnectProtocolException>();

    [Fact]
    public void A_non_site_audience_is_still_held_to_the_configured_client() =>
        Validate("dcms-admin", azp: "dcms-admin").Should().Throw<OpenIdConnectProtocolException>();

    private static Action Validate(string audience, string azp)
    {
        var token = new JwtSecurityToken("https://auth.example", audience,
            [new Claim("sub", "u"), new Claim("azp", azp), new Claim("iat", "1", ClaimValueTypes.Integer64)],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5));
        var validator = new SiteAuthentication.SiteProtocolValidator { RequireNonce = false, RequireStateValidation = false };
        return () => validator.ValidateTokenResponse(new OpenIdConnectProtocolValidationContext
        {
            ClientId = "site:",
            ProtocolMessage = new OpenIdConnectMessage { IdToken = "x", AccessToken = "y" },
            ValidatedIdToken = token,
        });
    }
}
