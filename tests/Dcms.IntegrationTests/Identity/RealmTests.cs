extern alias IdentityApp;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IdentityApp::Dcms.Identity.Realms;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;

namespace Dcms.IntegrationTests.Identity;

/// <summary>
/// Tenant realms (ADR 0022) against the real identity service: an invited enterprise user signs
/// in to their tenant's realm and the edge's client for that tenant gets tokens naming that realm
/// and only it. Accounts are strictly per tenant, a platform sign-in never satisfies a realm,
/// and disabling, lockout and group changes take effect where they must.
/// </summary>
[Collection(IdentityCollection.Name)]
public sealed class RealmTests(IdentityAppFixture fixture)
{
    private const string Password = "correct horse battery";

    [DockerFact]
    public async Task An_invited_user_signs_in_and_gets_tokens_for_their_realm_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await RealmAsync(ct);
        var staff = await GroupAsync(a, "Staff", ct);
        var (userId, invite) = await InviteAsync(a, $"{Guid.NewGuid():N}@corp.test", ct, staff);

        var browser = Browser();
        await AcceptAsync(browser, invite, ct);

        var (location, verifier) = await AuthorizeAsync(browser, a, ct);
        location.Should().StartWith($"https://{a.Host}/.edge/signin-oidc", "the invitation signed the user in to the realm");
        var tokens = await ExchangeAsync(a, location, verifier, ct);

        var access = Claims(tokens.GetProperty("access_token").GetString()!);
        access.GetProperty("sub").GetString().Should().Be(userId.ToString());
        access.GetProperty("realm").GetString().Should().Be(a.TenantId.ToString());
        Values(access, "aud").Should().Equal($"dcms.realm:{a.TenantId}");
        Values(access, "groups").Should().Equal(staff.ToString());
        Claims(tokens.GetProperty("id_token").GetString()!).TryGetProperty("realm_stamp", out _)
            .Should().BeFalse("the stamp stays inside identity's own code and refresh tokens");

        // The link was single-use: setting the password restamped the account.
        using var reused = await Browser().GetAsync(invite, ct);
        (await reused.Content.ReadAsStringAsync(ct)).Should().Contain("This link does not work");
    }

    [DockerFact]
    public async Task Accounts_belong_to_one_tenant_and_a_session_reaches_no_other()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await RealmAsync(ct);
        var b = await RealmAsync(ct);
        var email = $"{Guid.NewGuid():N}@corp.test";
        var (userA, invite) = await InviteAsync(a, email, ct);
        var browser = Browser();
        await AcceptAsync(browser, invite, ct);

        // Signed in to A; B's client finds no session in this browser.
        (await AuthorizeAsync(browser, b, ct)).Location.Should().StartWith($"/realm/{b.Slug}/login");

        // A's credentials mean nothing in B.
        (await PasswordSignInAsync(Browser(), b.Slug, email, Password, ct)).Should().Contain("error=1");

        // The same person can be invited to B, as a different account.
        var (userB, _) = await InviteAsync(b, email, ct);
        userB.Should().NotBe(userA);
    }

    [DockerFact]
    public async Task A_platform_sign_in_never_satisfies_a_realm()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await RealmAsync(ct);
        var browser = Browser();
        using (var page = await browser.GetAsync("/account/login", ct))
        {
            var token = Antiforgery(await page.Content.ReadAsStringAsync(ct));
            using var signedIn = await browser.PostAsync("/account/login", Form(("email", "admin@dcms.local"), ("password", "Admin!23456"),
                ("__RequestVerificationToken", token)), ct);
            signedIn.StatusCode.Should().Be(HttpStatusCode.Found);
        }

        (await AuthorizeAsync(browser, a, ct)).Location.Should().StartWith($"/realm/{a.Slug}/login",
            "the SuperAdmin's platform cookie is not an account in any tenant's realm");
    }

    [DockerFact]
    public async Task Disabling_a_user_ends_their_sessions_and_refreshes()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await RealmAsync(ct);
        var (userId, invite) = await InviteAsync(a, $"{Guid.NewGuid():N}@corp.test", ct);
        var browser = Browser();
        await AcceptAsync(browser, invite, ct);
        var (location, verifier) = await AuthorizeAsync(browser, a, ct);
        var tokens = await ExchangeAsync(a, location, verifier, ct);
        (await RefreshAsync(a, tokens, ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        await AdminAsync(HttpMethod.Patch, $"/api/realms/{a.TenantId}/users/{userId}", new { status = "disabled" }, ct);

        using var refused = await RefreshAsync(a, tokens, ct);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(ct)).Should().Contain("invalid_grant");
        (await AuthorizeAsync(browser, a, ct)).Location.Should().StartWith($"/realm/{a.Slug}/login", "the cookie is refused too");
        (await PasswordSignInAsync(Browser(), a.Slug, (await UserAsync(a, userId, ct)).GetProperty("email").GetString()!, Password, ct))
            .Should().Contain("error=1");
    }

    [DockerFact]
    public async Task Repeated_wrong_passwords_lock_the_account()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await RealmAsync(ct);
        var email = $"{Guid.NewGuid():N}@corp.test";
        var (userId, invite) = await InviteAsync(a, email, ct);
        await AcceptAsync(Browser(), invite, ct);

        for (var i = 0; i < RealmStore.MaxFailedAttempts; i++)
        {
            await PasswordSignInAsync(Browser(), a.Slug, email, "wrong password!", ct);
        }
        (await PasswordSignInAsync(Browser(), a.Slug, email, Password, ct)).Should().Contain("error=1", "locked, even with the right password");
        (await UserAsync(a, userId, ct)).GetProperty("lockedOut").GetBoolean().Should().BeTrue();
    }

    [DockerFact]
    public async Task A_group_change_reaches_the_next_refresh()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await RealmAsync(ct);
        var managers = await GroupAsync(a, "Managers", ct);
        var (userId, invite) = await InviteAsync(a, $"{Guid.NewGuid():N}@corp.test", ct);
        var browser = Browser();
        await AcceptAsync(browser, invite, ct);
        var (location, verifier) = await AuthorizeAsync(browser, a, ct);
        var tokens = await ExchangeAsync(a, location, verifier, ct);
        Values(Claims(tokens.GetProperty("access_token").GetString()!), "groups").Should().BeEmpty();

        await AdminAsync(HttpMethod.Put, $"/api/realms/{a.TenantId}/groups/{managers}/members/{userId}", null, ct);
        using var refreshed = await RefreshAsync(a, tokens, ct);
        var renewed = await refreshed.Content.ReadFromJsonAsync<JsonElement>(ct);
        Values(Claims(renewed.GetProperty("access_token").GetString()!), "groups").Should().Equal(managers.ToString());
    }

    [DockerFact]
    public async Task The_realm_admin_api_answers_only_the_realms_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await RealmAsync(ct);
        var client = fixture.Factory.CreateClient();

        using (var anonymous = await client.GetAsync($"/api/realms/{a.TenantId}", ct))
        {
            anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        // platform-api's service token: a real, valid token, without dcms.realms.
        var console = await ServiceTokenAsync("dcms-platform-api-service", "dcms-platform-api-dev-secret", "dcms.console", ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/realms/{a.TenantId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", console);
        using var forbidden = await client.SendAsync(request, ct);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- helpers ----

    private sealed record TestRealm(Guid TenantId, string Slug, string Host)
    {
        public string ClientId => RealmClients.IdFor(TenantId);
    }

    private async Task<TestRealm> RealmAsync(CancellationToken ct)
    {
        var tenantId = Guid.NewGuid();
        var slug = $"t{tenantId.ToString("N")[..12]}";
        var host = $"{slug}.sites.test";
        var info = await AdminAsync(HttpMethod.Put, $"/api/realms/{tenantId}", new { slug, name = $"Corp {slug}", hosts = new[] { host } }, ct);
        info.GetProperty("clientReady").GetBoolean().Should().BeTrue("the fixture holds the edge-sites master secret");
        return new TestRealm(tenantId, slug, host);
    }

    private async Task<Guid> GroupAsync(TestRealm realm, string name, CancellationToken ct) =>
        (await AdminAsync(HttpMethod.Post, $"/api/realms/{realm.TenantId}/groups", new { name }, ct)).GetProperty("id").GetGuid();

    private async Task<(Guid UserId, string InvitePath)> InviteAsync(TestRealm realm, string email, CancellationToken ct, params Guid[] groups)
    {
        var result = await AdminAsync(HttpMethod.Post, $"/api/realms/{realm.TenantId}/users/invite", new { email, displayName = "Pat", groups }, ct);
        var url = new Uri(result.GetProperty("inviteUrl").GetString()!);
        url.GetLeftPart(UriPartial.Authority).Should().Be(IdentityAppFixture.Issuer.TrimEnd('/'), "links point at identity's public origin");
        return (result.GetProperty("user").GetProperty("id").GetGuid(), url.PathAndQuery);
    }

    private Task<JsonElement> UserAsync(TestRealm realm, Guid userId, CancellationToken ct) =>
        AdminAsync(HttpMethod.Get, $"/api/realms/{realm.TenantId}/users/{userId}", null, ct);

    private async Task<JsonElement> AdminAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await ServiceTokenAsync("dcms-admin-api", "dcms-admin-api-dev-secret", "dcms.realms", ct));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        using var response = await fixture.Factory.CreateClient().SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        response.IsSuccessStatusCode.Should().BeTrue($"{method} {path}: {(int)response.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<string> ServiceTokenAsync(string clientId, string secret, string scope, CancellationToken ct)
    {
        using var response = await fixture.Factory.CreateClient().PostAsync("/connect/token",
            Form(("grant_type", "client_credentials"), ("client_id", clientId), ("client_secret", secret), ("scope", scope)), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("access_token").GetString()!;
    }

    private HttpClient Browser() => fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task AcceptAsync(HttpClient browser, string invitePath, CancellationToken ct)
    {
        using var page = await browser.GetAsync(invitePath, ct);
        var html = await page.Content.ReadAsStringAsync(ct);
        var token = Regex.Match(html, """name="token" value="([^"]+)""").Groups[1].Value;
        var slug = new Uri(new Uri("http://x"), invitePath).Segments[2].TrimEnd('/');
        using var accepted = await browser.PostAsync($"/realm/{slug}/invite", Form(
            ("token", WebUtility.HtmlDecode(token)), ("password", Password), ("confirmPassword", Password),
            ("__RequestVerificationToken", Antiforgery(html))), ct);
        accepted.StatusCode.Should().Be(HttpStatusCode.Found, await accepted.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Signs in on the realm's form; returns where it redirected (an error says so in the query).</summary>
    private static async Task<string> PasswordSignInAsync(HttpClient browser, string slug, string email, string password, CancellationToken ct)
    {
        using var page = await browser.GetAsync($"/realm/{slug}/login", ct);
        var html = await page.Content.ReadAsStringAsync(ct);
        using var response = await browser.PostAsync($"/realm/{slug}/login", Form(
            ("email", email), ("password", password), ("__RequestVerificationToken", Antiforgery(html))), ct);
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        return response.Headers.Location!.ToString();
    }

    private static async Task<(string Location, string Verifier)> AuthorizeAsync(HttpClient browser, TestRealm realm, CancellationToken ct)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var url = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = realm.ClientId,
            ["redirect_uri"] = $"https://{realm.Host}/.edge/signin-oidc",
            ["response_type"] = "code",
            ["scope"] = "openid profile email offline_access",
            ["state"] = "s",
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
        });
        using var response = await browser.GetAsync(url, ct);
        response.StatusCode.Should().Be(HttpStatusCode.Found, await response.Content.ReadAsStringAsync(ct));
        return (response.Headers.Location!.ToString(), verifier);
    }

    private async Task<JsonElement> ExchangeAsync(TestRealm realm, string location, string verifier, CancellationToken ct)
    {
        using var response = await fixture.Factory.CreateClient().PostAsync("/connect/token", Form(
            ("grant_type", "authorization_code"),
            ("code", QueryHelpers.ParseQuery(new Uri(location).Query)["code"].ToString()),
            ("redirect_uri", $"https://{realm.Host}/.edge/signin-oidc"),
            ("client_id", realm.ClientId),
            ("client_secret", RealmClients.SecretFor(IdentityAppFixture.EdgeSitesSecret, realm.ClientId)),
            ("code_verifier", verifier)), ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private Task<HttpResponseMessage> RefreshAsync(TestRealm realm, JsonElement tokens, CancellationToken ct) =>
        fixture.Factory.CreateClient().PostAsync("/connect/token", Form(
            ("grant_type", "refresh_token"),
            ("refresh_token", tokens.GetProperty("refresh_token").GetString()!),
            ("client_id", realm.ClientId),
            ("client_secret", RealmClients.SecretFor(IdentityAppFixture.EdgeSitesSecret, realm.ClientId))), ct);

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    private static string Antiforgery(string html)
    {
        var token = Regex.Match(html, """name="__RequestVerificationToken" value="([^"]+)""").Groups[1].Value;
        token.Should().NotBeEmpty();
        return WebUtility.HtmlDecode(token);
    }

    private static JsonElement Claims(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement.Clone();
    }

    /// <summary>A claim that may be one string or a list of them.</summary>
    private static List<string?> Values(JsonElement claims, string name) =>
        !claims.TryGetProperty(name, out var value) ? []
        : value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(v => v.GetString()).ToList()
        : [value.GetString()];

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
