extern alias IdentityApp;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dcms.IntegrationTests.Social;
using Dcms.Shared.Vault;
using IdentityApp::Dcms.Identity.Realms;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Identity;

/// <summary>
/// Signing in to a realm through the tenant's own provider (ADR 0022, UA2), with the real
/// OpenIdConnect handler against a stub provider: only invited or allowed people get in, only on
/// an email the provider vouches for; links survive an email change; mapped groups follow the
/// provider; secrets never come back out; and "Sign in with DCMS" links only through an
/// invitation, from a round trip this browser started.
/// </summary>
[Collection(IdentityCollection.Name)]
public sealed class RealmProviderTests(IdentityAppFixture fixture) : IDisposable
{
    private readonly StubOidcProvider _idp = new();
    private WebApplicationFactory<IdentityApp::Program>? _factory;

    private WebApplicationFactory<IdentityApp::Program> Factory => _factory ??= fixture.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
    {
        s.AddSingleton<ITransitEncryptor, FakeTransitEncryptor>();
        s.AddSingleton(new RealmOidcBackchannel(_idp));
    }));

    public void Dispose() => _factory?.Dispose();

    [DockerFact]
    public async Task Only_invited_people_get_in_and_a_link_outlives_an_email_change()
    {
        var ct = TestContext.Current.CancellationToken;
        var realm = await RealmAsync(ct);
        await ProviderAsync(realm, "corp", new { kind = "oidc", displayName = "Corp SSO", clientId = "corp-client", clientSecret = "s3cret", issuer = _idp.Issuer }, ct);

        // A stranger the provider vouches for is still a stranger here.
        (await SignInAsync(realm, "corp", "corp-client", Person("stranger", "stranger@corp.test"), ct)).Status.Should().Be(HttpStatusCode.Forbidden);

        // Invited: the provider's verified email finds the invitation, and accepting it is signing in.
        var email = $"{Guid.NewGuid():N}@corp.test";
        var userId = await InviteAsync(realm, email, ct);
        var first = await SignInAsync(realm, "corp", "corp-client", Person("u-1", email), ct);
        first.Location.Should().Be($"/realm/{realm.Slug}/");
        (await UserAsync(realm, userId, ct)).GetProperty("status").GetString().Should().Be("active");

        // The link is to the provider's subject, so a renamed address still signs in.
        (await SignInAsync(realm, "corp", "corp-client", Person("u-1", "renamed@elsewhere.test"), ct)).Location.Should().Be($"/realm/{realm.Slug}/");
    }

    [DockerFact]
    public async Task An_email_the_provider_has_not_verified_never_finds_an_account()
    {
        var ct = TestContext.Current.CancellationToken;
        var realm = await RealmAsync(ct);
        await ProviderAsync(realm, "corp", new { kind = "oidc", displayName = "Corp", clientId = "c", clientSecret = "s", issuer = _idp.Issuer }, ct);
        var email = $"{Guid.NewGuid():N}@corp.test";
        await InviteAsync(realm, email, ct);

        (await SignInAsync(realm, "corp", "c", Person("attacker", email, verified: false), ct)).Status
            .Should().Be(HttpStatusCode.Forbidden, "anyone can type an address into an account at a provider that does not check it");
    }

    [DockerFact]
    public async Task Allowed_domains_create_accounts_with_default_and_mapped_groups()
    {
        var ct = TestContext.Current.CancellationToken;
        var realm = await RealmAsync(ct);
        var staff = await GroupAsync(realm, "Staff", ct);
        var engineers = await GroupAsync(realm, "Engineers", ct);
        await ProviderAsync(realm, "corp", new
        {
            kind = "oidc", displayName = "Corp", clientId = "c", clientSecret = "s", issuer = _idp.Issuer,
            provisioning = "allowedDomains", allowedDomains = new[] { "corp.test" }, defaultGroups = new[] { staff },
            groupClaim = "groups", groupMappings = new Dictionary<string, Guid> { ["eng"] = engineers },
        }, ct);

        (await SignInAsync(realm, "corp", "c", Person("x", "x@other.test"), ct)).Status.Should().Be(HttpStatusCode.Forbidden, "not an allowed domain");

        var email = $"{Guid.NewGuid():N}@corp.test";
        (await SignInAsync(realm, "corp", "c", Person("new-1", email, groups: ["eng"]), ct)).Location.Should().Be($"/realm/{realm.Slug}/");
        var user = (await AdminAsync(HttpMethod.Get, $"/api/realms/{realm.TenantId}/users?search={email}", null, ct)).GetProperty("items")[0];
        user.GetProperty("groups").EnumerateArray().Select(g => g.GetGuid()).Should().BeEquivalentTo([staff, engineers]);

        // Left "eng" at the provider: the mapped group follows; the realm's own grant stays.
        await SignInAsync(realm, "corp", "c", Person("new-1", email), ct);
        (await UserAsync(realm, user.GetProperty("id").GetGuid(), ct)).GetProperty("groups").EnumerateArray().Select(g => g.GetGuid())
            .Should().Equal(staff);
    }

    [DockerFact]
    public async Task Secrets_go_in_and_never_come_back_out()
    {
        var ct = TestContext.Current.CancellationToken;
        var realm = await RealmAsync(ct);
        var saved = await ProviderAsync(realm, "google", new { kind = "google", displayName = "Google", clientId = "g", clientSecret = "very-secret", hostedDomain = "corp.test" }, ct);
        saved.GetProperty("hasSecret").GetBoolean().Should().BeTrue();
        saved.GetProperty("callbackUrl").GetString().Should().StartWith(IdentityAppFixture.Issuer.TrimEnd('/') + "/realm/sso/");
        (await AdminAsync(HttpMethod.Get, $"/api/realms/{realm.TenantId}/providers", null, ct)).ToString().Should().NotContain("very-secret");

        // A directory-less Entra would let any Microsoft account vouch for any address.
        using var request = await AdminRequestAsync(HttpMethod.Put, $"/api/realms/{realm.TenantId}/providers/ms",
            new { kind = "entra", displayName = "Microsoft", clientId = "m", clientSecret = "s", entraTenant = "common" }, ct);
        using var refused = await Factory.CreateClient().SendAsync(request, ct);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task Sign_in_with_DCMS_links_through_an_invitation_from_this_browser_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var realm = await RealmAsync(ct);
        await ProviderAsync(realm, "dcms", new { kind = "dcms", displayName = "DCMS" }, ct);
        var userId = await InviteAsync(realm, $"{Guid.NewGuid():N}@corp.test", ct);
        var invite = await InviteTokenAsync(realm, userId, ct);

        var browser = Browser();
        await PlatformSignInAsync(browser, ct);

        // A completion URL someone planted, with no round trip behind it, links nothing.
        using (var planted = await browser.GetAsync($"/realm/{realm.Slug}/sso/dcms/complete?invite={Uri.EscapeDataString(invite)}&state=guess", ct))
        {
            planted.Headers.Location!.ToString().Should().Contain("error=sso");
        }

        using var start = await browser.GetAsync($"/realm/{realm.Slug}/sso/dcms?invite={Uri.EscapeDataString(invite)}", ct);
        var login = start.Headers.Location!.ToString();
        login.Should().StartWith("/account/login?returnUrl=");
        var complete = QueryHelpers.ParseQuery(login[(login.IndexOf('?') + 1)..])["returnUrl"].ToString();
        using var done = await browser.GetAsync(complete, ct);
        done.Headers.Location!.ToString().Should().Be($"/realm/{realm.Slug}/");
        (await UserAsync(realm, userId, ct)).GetProperty("status").GetString().Should().Be("active");
    }

    // ---- the round trip ----

    private static Dictionary<string, object> Person(string sub, string email, bool verified = true, string[]? groups = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = sub, ["email"] = email, ["email_verified"] = verified, ["name"] = "Pat" };
        if (groups is not null)
        {
            claims["groups"] = groups;
        }
        return claims;
    }

    /// <summary>
    /// Out to the provider and back, as a browser does it: the challenge, the provider's form
    /// post to the callback, and the realm's decision. Returns the decision's status and redirect.
    /// </summary>
    private async Task<(HttpStatusCode Status, string? Location)> SignInAsync(TestRealm realm, string key, string clientId,
        Dictionary<string, object> who, CancellationToken ct)
    {
        var browser = Browser();
        using var challenge = await browser.GetAsync($"/realm/{realm.Slug}/sso/{key}", ct);
        challenge.StatusCode.Should().Be(HttpStatusCode.Found, await challenge.Content.ReadAsStringAsync(ct));
        var authorize = challenge.Headers.Location!;
        authorize.GetLeftPart(UriPartial.Path).Should().Be($"{_idp.Issuer}/authorize");
        var query = QueryHelpers.ParseQuery(authorize.Query);

        var code = Guid.NewGuid().ToString("N");
        _idp.Issue(code, query["nonce"].ToString(), clientId, who);
        using var callback = await browser.PostAsync(new Uri(query["redirect_uri"].ToString()).PathAndQuery,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["state"] = query["state"].ToString() }), ct);
        callback.StatusCode.Should().Be(HttpStatusCode.Found, await callback.Content.ReadAsStringAsync(ct));

        using var decision = await browser.GetAsync(callback.Headers.Location!.ToString(), ct);
        decision.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError, await decision.Content.ReadAsStringAsync(ct));
        return (decision.StatusCode, decision.Headers.Location?.ToString());
    }

    private HttpClient Browser() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        // https: the handler's correlation and nonce cookies are Secure.
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    private static async Task PlatformSignInAsync(HttpClient browser, CancellationToken ct)
    {
        using var page = await browser.GetAsync("/account/login", ct);
        var token = Regex.Match(await page.Content.ReadAsStringAsync(ct), """name="__RequestVerificationToken" value="([^"]+)""").Groups[1].Value;
        using var signedIn = await browser.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = "admin@dcms.local", ["password"] = "Admin!23456", ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token),
        }), ct);
        signedIn.StatusCode.Should().Be(HttpStatusCode.Found);
    }

    // ---- the realm admin API ----

    private sealed record TestRealm(Guid TenantId, string Slug);

    private async Task<TestRealm> RealmAsync(CancellationToken ct)
    {
        var tenantId = Guid.NewGuid();
        var slug = $"p{tenantId.ToString("N")[..12]}";
        await AdminAsync(HttpMethod.Put, $"/api/realms/{tenantId}", new { slug, name = "Corp", hosts = new[] { $"{slug}.sites.test" } }, ct);
        return new TestRealm(tenantId, slug);
    }

    private Task<JsonElement> ProviderAsync(TestRealm realm, string key, object body, CancellationToken ct) =>
        AdminAsync(HttpMethod.Put, $"/api/realms/{realm.TenantId}/providers/{key}", body, ct);

    private async Task<Guid> GroupAsync(TestRealm realm, string name, CancellationToken ct) =>
        (await AdminAsync(HttpMethod.Post, $"/api/realms/{realm.TenantId}/groups", new { name }, ct)).GetProperty("id").GetGuid();

    private async Task<Guid> InviteAsync(TestRealm realm, string email, CancellationToken ct) =>
        (await AdminAsync(HttpMethod.Post, $"/api/realms/{realm.TenantId}/users/invite", new { email }, ct)).GetProperty("user").GetProperty("id").GetGuid();

    private async Task<string> InviteTokenAsync(TestRealm realm, Guid userId, CancellationToken ct)
    {
        var url = (await AdminAsync(HttpMethod.Post, $"/api/realms/{realm.TenantId}/users/{userId}/invite", null, ct)).GetProperty("inviteUrl").GetString()!;
        return QueryHelpers.ParseQuery(new Uri(url).Query)["token"].ToString();
    }

    private Task<JsonElement> UserAsync(TestRealm realm, Guid userId, CancellationToken ct) =>
        AdminAsync(HttpMethod.Get, $"/api/realms/{realm.TenantId}/users/{userId}", null, ct);

    private async Task<JsonElement> AdminAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = await AdminRequestAsync(method, path, body, ct);
        using var response = await Factory.CreateClient().SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        response.IsSuccessStatusCode.Should().BeTrue($"{method} {path}: {(int)response.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<HttpRequestMessage> AdminRequestAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var tokenResponse = await Factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = "dcms-admin-api", ["client_secret"] = "dcms-admin-api-dev-secret", ["scope"] = "dcms.realms",
        }), ct);
        var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("access_token").GetString();
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return request;
    }
}
