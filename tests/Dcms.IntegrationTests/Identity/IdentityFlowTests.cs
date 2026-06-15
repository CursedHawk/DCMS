using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Dcms.IntegrationTests.Identity;

[Collection(IdentityCollection.Name)]
public class IdentityFlowTests(IdentityAppFixture fixture)
{
    [DockerFact]
    public async Task Discovery_document_advertises_issuer_and_grants()
    {
        var client = fixture.Factory.CreateClient();

        var doc = await client.GetFromJsonAsync<JsonElement>(
            ".well-known/openid-configuration", TestContext.Current.CancellationToken);

        doc.GetProperty("issuer").GetString().Should().Be(IdentityAppFixture.Issuer);
        var grants = doc.GetProperty("grant_types_supported").EnumerateArray().Select(e => e.GetString());
        grants.Should().Contain(["authorization_code", "refresh_token", "client_credentials"]);
        var scopes = doc.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString());
        scopes.Should().Contain(["dcms.admin", "dcms.ai"]);
    }

    [DockerFact]
    public async Task Client_credentials_grant_issues_ai_scoped_jwt()
    {
        var client = fixture.Factory.CreateClient();

        using var response = await client.PostAsync("connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "dcms-admin-api",
                ["client_secret"] = "dcms-admin-api-dev-secret",
                ["scope"] = "dcms.ai",
            }), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var accessToken = payload.GetProperty("access_token").GetString();
        accessToken.Should().NotBeNullOrEmpty();

        var claims = DecodeJwtPayload(accessToken!);
        claims.GetProperty("iss").GetString().Should().Be(IdentityAppFixture.Issuer);
        AudienceValues(claims).Should().Contain("dcms-ai-gateway");
    }

    // Reads the JWT payload segment without a validation library (we only need
    // to assert claims; signature validation is covered by JwtBearer at runtime).
    private static JsonElement DecodeJwtPayload(string jwt)
    {
        var segment = jwt.Split('.')[1];
        var padded = segment.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static IEnumerable<string?> AudienceValues(JsonElement claims)
    {
        if (!claims.TryGetProperty("aud", out var aud))
        {
            return [];
        }
        return aud.ValueKind == JsonValueKind.Array
            ? aud.EnumerateArray().Select(e => e.GetString())
            : [aud.GetString()];
    }

    [DockerFact]
    public async Task Client_credentials_grant_rejects_bad_secret()
    {
        var client = fixture.Factory.CreateClient();

        using var response = await client.PostAsync("connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "dcms-admin-api",
                ["client_secret"] = "wrong-secret",
                ["scope"] = "dcms.ai",
            }), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
