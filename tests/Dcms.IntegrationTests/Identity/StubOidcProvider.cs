using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Dcms.IntegrationTests.Identity;

/// <summary>
/// A tenant's identity provider, in process: discovery, keys and a token endpoint that answers
/// codes the test registered, with ID tokens signed by a real RSA key. Plugged into identity's
/// back channel, so the real OpenIdConnect handler does every check it does in production —
/// state, nonce, PKCE, issuer, audience, signature — against it.
/// </summary>
public sealed class StubOidcProvider : HttpMessageHandler
{
    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "stub-key" };
    private readonly ConcurrentDictionary<string, (string Nonce, string ClientId, IReadOnlyDictionary<string, object> Claims)> _codes = new();

    public string Issuer { get; }

    public StubOidcProvider(string issuer = "https://idp.test") => Issuer = issuer;

    /// <summary>What the provider will say about whoever exchanges <paramref name="code"/>.</summary>
    public void Issue(string code, string nonce, string clientId, IReadOnlyDictionary<string, object> claims) =>
        _codes[code] = (nonce, clientId, claims);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
        {
            return Json(new Dictionary<string, object>
            {
                ["issuer"] = Issuer,
                ["authorization_endpoint"] = $"{Issuer}/authorize",
                ["token_endpoint"] = $"{Issuer}/token",
                ["jwks_uri"] = $"{Issuer}/jwks",
                ["response_types_supported"] = new[] { "code" },
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            });
        }
        if (path.EndsWith("/jwks", StringComparison.Ordinal))
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_key);
            return Json(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } } });
        }
        if (path.EndsWith("/token", StringComparison.Ordinal))
        {
            var form = await request.Content!.ReadFromFormAsync(ct);
            if (!_codes.TryRemove(form["code"], out var issued) || form["client_id"] != issued.ClientId)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invalid_grant" }) };
            }
            var claims = new Dictionary<string, object>(issued.Claims) { ["nonce"] = issued.Nonce };
            var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = issued.ClientId,
                Claims = claims,
                IssuedAt = DateTime.UtcNow,
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
            });
            return Json(new { access_token = "stub-access", token_type = "Bearer", expires_in = 300, id_token = idToken });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
}

internal static class FormReading
{
    public static async Task<Dictionary<string, string>> ReadFromFormAsync(this HttpContent content, CancellationToken ct) =>
        (await content.ReadAsStringAsync(ct)).Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => p.Length > 1 ? WebUtility.UrlDecode(p[1]) : string.Empty);
}
