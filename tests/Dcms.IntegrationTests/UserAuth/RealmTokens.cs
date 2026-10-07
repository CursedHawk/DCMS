using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dcms.IntegrationTests.UserAuth;

/// <summary>
/// Realm access tokens as identity mints them (sub, realm, aud = dcms.realm:{tenant}, groups),
/// signed by a test key content-api's DcmsUser scheme is told to trust in place of identity's.
/// </summary>
public static class RealmTokens
{
    public const string Issuer = "https://identity.test/";

    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "realm-test" };

    public static void Trust(JwtBearerOptions options)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
        configuration.SigningKeys.Add(Key);
        options.Configuration = configuration;
        options.TokenValidationParameters.ValidIssuer = Issuer;
        options.TokenValidationParameters.IssuerSigningKey = Key;
    }

    public static string Mint(Guid tenantId, Guid userId, IEnumerable<Guid>? groups = null, string? audience = null, string? realm = null,
        string email = "pat@corp.test", DateTime? expires = null)
    {
        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("realm", realm ?? tenantId.ToString()),
            new("email", email),
            new("name", "Pat"),
        };
        claims.AddRange((groups ?? []).Select(g => new Claim("groups", g.ToString())));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience ?? $"dcms.realm:{tenantId}",
            Subject = new ClaimsIdentity(claims),
            NotBefore = (expires ?? DateTime.UtcNow.AddMinutes(10)).AddMinutes(-20),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256),
        });
    }
}
