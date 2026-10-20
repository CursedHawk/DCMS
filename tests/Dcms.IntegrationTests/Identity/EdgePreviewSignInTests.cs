extern alias EdgeApp;
extern alias IdentityApp;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dcms.Shared.Vault;
using EdgeApp::Dcms.Edge;
using EdgeApp::Dcms.Edge.Auth;
using IdentityApp::Dcms.Identity.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Dcms.IntegrationTests.Identity;

/// <summary>
/// A site builder's preview, signed in as a site user (#269), through the real edge and a real
/// identity: the realm's own sign-in run on the console's host, "View as" with a token identity
/// mints, the primary domain's rules, and the token a preview's API call carries. admin-api is a
/// stand-in that answers the two questions the edge asks it, as the member.
/// </summary>
public sealed class EdgePreviewSignInTests : IAsyncLifetime
{
    private const string Secret = "edge-test-secret";
    private const string SitesSecret = "edge-sites-test-secret";
    private const string AdminHost = "admin.example.test";
    private const string IdentityHost = "identity.test";
    private const string SiteHost = "portal.corp.test";
    private const string Tenant = "corp";
    private const string Ada = "ada@example.test";
    private const string Password = "Correct-Horse-Battery-9";
    private const string RealmPassword = "correct horse battery";

    private static readonly Uri Admin = new($"https://{AdminHost}/");
    private static readonly Uri Auth = new($"http://{IdentityHost}/");
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid Site = Guid.NewGuid();

    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();
    private readonly string _keyRing = Path.Combine(Path.GetTempPath(), $"dcms-edge-preview-{Guid.NewGuid():N}");
    private WebApplicationFactory<IdentityApp::Program> _identity = null!;
    private ConnectionMultiplexer _connection = null!;
    private WebApplication _edge = null!;
    private string _ada = "";

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        _identity = new WebApplicationFactory<IdentityApp::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
            builder.UseSetting("Identity:Issuer", IdentityAppFixture.Issuer);
            builder.UseSetting("Identity:AllowInsecureHttp", "true");
            builder.UseSetting("Identity:Migrate", "true");
            builder.UseSetting("Identity:Seed", "true");
            builder.UseSetting("Identity:Edge:Secret", Secret);
            builder.UseSetting("Identity:Edge:RedirectUris", $"https://{AdminHost}/.edge/signin-oidc");
            builder.UseSetting("Identity:EdgeSites:Secret", SitesSecret);
            builder.UseSetting("Identity:EdgeSites:PreviewHost", AdminHost);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Nats:Url"] = "nats://localhost:4222" }));
            builder.ConfigureTestServices(services => services
                .AddDataProtection()
                .PersistKeysToFileSystem(Directory.CreateDirectory(Path.Combine(_keyRing, "identity"))));
        });
        using var _ = _identity.CreateClient();

        await using (var scope = _identity.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<DcmsUser>>();
            var user = new DcmsUser { UserName = Ada, Email = Ada, EmailConfirmed = true };
            (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();
            _ada = user.Id.ToString();
        }
        await RealmAdminAsync(HttpMethod.Put, "", new { slug = Tenant, name = "Corp", hosts = new[] { SiteHost } }, default);

        _connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        _edge = BuildEdge();
        await _edge.StartAsync();
    }

    private WebApplication BuildEdge()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Edge:AdminHost"] = AdminHost,
            ["Edge:Upstreams:AdminApi"] = "http://admin-api.test",
            ["Edge:Auth:Authority"] = IdentityAppFixture.Issuer,
            ["Edge:Auth:InternalAuthority"] = $"http://{IdentityHost}",
            ["Edge:Auth:ClientSecret"] = Secret,
            ["Edge:Auth:SitesSecret"] = SitesSecret,
            ["Edge:Auth:Bff"] = "true",
        });
        var authOptions = builder.Configuration.GetSection(EdgeAuthOptions.SectionName).Get<EdgeAuthOptions>()!;
        builder.Services.Configure<EdgeOptions>(builder.Configuration.GetSection(EdgeOptions.SectionName));
        builder.Services.Configure<EdgeAuthOptions>(builder.Configuration.GetSection(EdgeAuthOptions.SectionName));
        builder.Services.AddSingleton<IConnectionMultiplexer>(_connection);
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<ITransitEncryptor, NoTransit>();
        builder.AddEdgeAuthentication();
        builder.AddBff();
        builder.AddSiteAuthentication(authOptions);
        builder.AddPreviewAuthentication(authOptions);
        // No database here: the rules are set below, not loaded.
        builder.Services.Remove(builder.Services.Single(d => d.ImplementationType == typeof(SiteGateLoader)));

        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(Directory.CreateDirectory(Path.Combine(_keyRing, "edge")));
        builder.Services.Configure<KeyManagementOptions>(o => o.XmlEncryptor = null);
        foreach (var scheme in new[] { OpenIdConnectDefaults.AuthenticationScheme, SiteAuthentication.OidcScheme, PreviewAuthentication.OidcScheme })
        {
            builder.Services.Configure<OpenIdConnectOptions>(scheme, o =>
            {
                o.Backchannel = new HttpClient(_identity.Server.CreateHandler());
                o.RequireHttpsMetadata = false;
            });
        }
        builder.Services.AddHttpClient<LoginSessionCheck>()
            .ConfigurePrimaryHttpMessageHandler(() => _identity.Server.CreateHandler());
        builder.Services.AddHttpClient<PreviewTargets>()
            .ConfigurePrimaryHttpMessageHandler(() => new FakeAdminApi(this));

        var app = builder.Build();
        app.Services.GetRequiredService<SiteGates>().Replace(new Dictionary<string, SiteGateEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [SiteHost] = new(TenantId, Tenant, [new SiteRule("/portal", SiteAccess.SignedIn)]),
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapEdgeAuthEndpoints();
        app.MapBffEndpoints();
        app.MapPreviewAuthEndpoints();
        // What the proxy pipeline would put on a preview's API call.
        app.MapGet("/api/admin/sites/{siteId:guid}/preview/api/whoami", async Task<IResult> (HttpContext context) =>
            Results.Text(await PreviewAuthentication.PreviewRealmTokenAsync(context) ?? ""));
        return app;
    }

    public async ValueTask DisposeAsync()
    {
        await _edge.DisposeAsync();
        await _identity.DisposeAsync();
        await _connection.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
        try { Directory.Delete(_keyRing, recursive: true); } catch (IOException) { }
    }

    [DockerFact]
    public async Task A_preview_signs_in_with_the_realms_own_sign_in_and_follows_the_primary_domains_rules()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = new Browser(_identity.Server.CreateHandler(), _edge.GetTestServer().CreateHandler());
        await SignInToConsoleAsync(browser, ct);
        var pat = await RealmUserAsync(browser, ct);

        var me = await JsonAsync(browser, $"/.edge/preview/{Tenant}/{Site}/me", ct);
        me.GetProperty("available").GetBoolean().Should().BeTrue();
        me.GetProperty("signedIn").GetBoolean().Should().BeFalse("a console session is nobody on the site");
        (await AccessAsync(browser, "/portal", ct)).Should().Be("signin", "the primary domain's rules apply to the preview");
        (await AccessAsync(browser, "/about", ct)).Should().Be("allow");

        // The popup: the realm's sign-in (already signed in to the realm at identity), back to the done page.
        var done = await browser.GetAsync(new Uri(Admin, $"/.edge/preview/{Tenant}/{Site}/signin"), ct);
        done.Uri.AbsolutePath.Should().Be(PreviewAuthentication.DonePath, done.Body);
        done.Body.Should().Contain(PreviewAuthentication.DoneMessage);

        me = await JsonAsync(browser, $"/.edge/preview/{Tenant}/{Site}/me", ct);
        me.GetProperty("signedIn").GetBoolean().Should().BeTrue();
        me.GetProperty("email").GetString().Should().Be(pat.Email);
        me.GetProperty("viewAs").GetBoolean().Should().BeFalse();
        (await AccessAsync(browser, "/portal", ct)).Should().Be("allow");
        Claims(await WhoAmIAsync(browser, ct)).GetProperty("sub").GetString().Should().Be(pat.Id.ToString(),
            "the preview's API calls carry the user's realm token");

        // Nobody else's preview: another site, another host, or no console session.
        (await browser.SendRawAsync(new Uri(Admin, $"/.edge/preview/{Tenant}/{Guid.NewGuid()}/me"), ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await browser.SendRawAsync(new Uri($"https://{SiteHost}/.edge/preview/{Tenant}/{Site}/me"), ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var stranger = new Browser(_identity.Server.CreateHandler(), _edge.GetTestServer().CreateHandler());
        (await stranger.SendRawAsync(new Uri(Admin, $"/.edge/preview/{Tenant}/{Site}/me"), ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var signedOut = await browser.GetAsync(new Uri(Admin, $"/.edge/preview/{Tenant}/{Site}/signout"), ct);
        signedOut.Uri.AbsolutePath.Should().Be(PreviewAuthentication.DonePath, signedOut.Body);
        (await JsonAsync(browser, $"/.edge/preview/{Tenant}/{Site}/me", ct)).GetProperty("signedIn").GetBoolean().Should().BeFalse();
        (await WhoAmIAsync(browser, ct)).Should().BeEmpty();
    }

    [DockerFact]
    public async Task View_as_takes_identitys_token_for_the_user_and_needs_the_consoles_csrf()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = new Browser(_identity.Server.CreateHandler(), _edge.GetTestServer().CreateHandler());
        await SignInToConsoleAsync(browser, ct);
        var pat = await RealmUserAsync(new Browser(_identity.Server.CreateHandler(), _edge.GetTestServer().CreateHandler()), ct);
        var csrf = await CsrfAsync(browser, ct);

        (await ViewAsAsync(browser, pat.Id, csrf: null, ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "a page elsewhere cannot make the preview someone");

        using (var viewed = await ViewAsAsync(browser, pat.Id, csrf, ct))
        {
            viewed.StatusCode.Should().Be(HttpStatusCode.OK, await viewed.Content.ReadAsStringAsync(ct));
        }
        var me = await JsonAsync(browser, $"/.edge/preview/{Tenant}/{Site}/me", ct);
        me.GetProperty("signedIn").GetBoolean().Should().BeTrue();
        me.GetProperty("viewAs").GetBoolean().Should().BeTrue();
        me.GetProperty("email").GetString().Should().Be(pat.Email);
        var token = Claims(await WhoAmIAsync(browser, ct));
        token.GetProperty("sub").GetString().Should().Be(pat.Id.ToString());
        token.GetProperty("act").GetProperty("sub").GetString().Should().Be(_ada, "identity was told who asked");

        // A new console session in this browser does not inherit the preview's site user.
        await SignInToConsoleAsync(browser, ct);
        (await WhoAmIAsync(browser, ct)).Should().BeEmpty("the preview session belonged to the console session that made it");
        (await JsonAsync(browser, $"/.edge/preview/{Tenant}/{Site}/me", ct)).GetProperty("signedIn").GetBoolean().Should().BeFalse();
        csrf = await CsrfAsync(browser, ct);

        // Someone who cannot sign in has no site to preview.
        var invited = await InviteAsync($"{Guid.NewGuid():N}@corp.test", ct);
        (await ViewAsAsync(browser, invited.Id, csrf, ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Signing out of a View-as session is the edge's alone: there is no realm sign-in to end.
        (await browser.GetAsync(new Uri(Admin, $"/.edge/preview/{Tenant}/{Site}/signout"), ct)).Uri.AbsolutePath.Should().Be(PreviewAuthentication.DonePath);
        (await JsonAsync(browser, $"/.edge/preview/{Tenant}/{Site}/me", ct)).GetProperty("signedIn").GetBoolean().Should().BeFalse();
    }

    // ---- helpers ----

    private sealed record RealmUser(Guid Id, string Email);

    private static async Task SignInToConsoleAsync(Browser browser, CancellationToken ct)
    {
        var page = await browser.GetAsync(new Uri(Admin, "/.edge/signin?returnUrl=/"), ct);
        if (page.Uri.Host == AdminHost)
        {
            return; // identity still knew the browser: a new console session, without a form
        }
        var landed = await browser.PostAsync(new Uri(Auth, "/account/login"), new Dictionary<string, string>
        {
            ["email"] = Ada,
            ["password"] = Password,
            ["returnUrl"] = QueryHelpers.ParseQuery(page.Uri.Query)["returnUrl"].ToString(),
            ["__RequestVerificationToken"] = Antiforgery(page.Body),
        }, ct);
        landed.Uri.Host.Should().Be(AdminHost, landed.Body);
    }

    /// <summary>A realm user who accepts their invitation in this browser — so the browser is signed in to the realm at identity.</summary>
    private async Task<RealmUser> RealmUserAsync(Browser browser, CancellationToken ct)
    {
        var (user, invite) = await InviteWithLinkAsync($"{Guid.NewGuid():N}@corp.test", ct);
        var page = await browser.GetAsync(new Uri(Auth, invite), ct);
        var token = Regex.Match(page.Body, """name="token" value="([^"]+)""").Groups[1].Value;
        await browser.PostAsync(new Uri(Auth, $"/realm/{Tenant}/invite"), new Dictionary<string, string>
        {
            ["token"] = WebUtility.HtmlDecode(token),
            ["password"] = RealmPassword,
            ["confirmPassword"] = RealmPassword,
            ["__RequestVerificationToken"] = Antiforgery(page.Body),
        }, ct);
        return user;
    }

    private async Task<RealmUser> InviteAsync(string email, CancellationToken ct) => (await InviteWithLinkAsync(email, ct)).User;

    private async Task<(RealmUser User, string InvitePath)> InviteWithLinkAsync(string email, CancellationToken ct)
    {
        var result = await RealmAdminAsync(HttpMethod.Post, "/users/invite", new { email, displayName = "Pat" }, ct);
        var user = result.GetProperty("user");
        return (new RealmUser(user.GetProperty("id").GetGuid(), email), new Uri(result.GetProperty("inviteUrl").GetString()!).PathAndQuery);
    }

    private async Task<JsonElement> RealmAdminAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await RealmAdminSendAsync(method, path, body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        response.IsSuccessStatusCode.Should().BeTrue($"{method} {path}: {(int)response.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<HttpResponseMessage> RealmAdminSendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var client = _identity.CreateClient();
        using var tokenResponse = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = "dcms-realm-admin",
            ["client_secret"] = "dcms-realm-admin-dev-secret", ["scope"] = "dcms.realms",
        }), ct);
        var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("access_token").GetString();
        var request = new HttpRequestMessage(method, $"/api/realms/{TenantId}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await client.SendAsync(request, ct);
    }

    private static async Task<JsonElement> JsonAsync(Browser browser, string path, CancellationToken ct)
    {
        using var response = await browser.SendRawAsync(new Uri(Admin, path), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string?> AccessAsync(Browser browser, string path, CancellationToken ct) =>
        (await JsonAsync(browser, $"/.edge/preview/{Tenant}/{Site}/access?path={Uri.EscapeDataString(path)}", ct)).GetProperty(path).GetString();

    private static async Task<string> WhoAmIAsync(Browser browser, CancellationToken ct)
    {
        using var response = await browser.SendRawAsync(new Uri(Admin, $"/api/admin/sites/{Site}/preview/api/whoami"), ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>The console's CSRF token, as the SPA reads it from the cookie /.edge/me sets.</summary>
    private static async Task<string> CsrfAsync(Browser browser, CancellationToken ct)
    {
        using var response = await browser.SendRawAsync(new Uri(Admin, "/.edge/me"), ct);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("dcms.csrf=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(cookie["dcms.csrf=".Length..cookie.IndexOf(';')]);
    }

    private static Task<HttpResponseMessage> ViewAsAsync(Browser browser, Guid userId, string? csrf, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Admin, $"/.edge/preview/{Tenant}/{Site}/view-as"))
        {
            Content = JsonContent.Create(new { instance = "users", userId }),
        };
        request.Headers.Add("Origin", $"https://{AdminHost}");
        if (csrf is not null)
        {
            request.Headers.Add(BffGuard.CsrfHeaderName, csrf);
        }
        return browser.SendAsync(request, ct);
    }

    private static string Antiforgery(string page)
        => WebUtility.HtmlDecode(Regex.Match(page, """name="__RequestVerificationToken" value="([^"]+)""").Groups[1].Value);

    private static JsonElement Claims(string jwt)
    {
        jwt.Should().NotBeEmpty();
        return JsonDocument.Parse(WebEncoders.Base64UrlDecode(jwt.Split('.')[1])).RootElement.Clone();
    }

    /// <summary>
    /// admin-api, as far as the edge asks it: the preview's site (for a member's bearer, in the
    /// right tenant) and User Authentication's "View as" relay to identity's realm admin API.
    /// </summary>
    private sealed class FakeAdminApi(EdgePreviewSignInTests test) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Headers.Authorization?.Scheme != "Bearer" || !request.Headers.TryGetValues("X-Dcms-Tenant", out var tenant) || tenant.Single() != Tenant)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            // As admin-api answers a person's write without one (IdempotencyMiddleware, RequireForUsers).
            if (request.Method != HttpMethod.Get && !request.Headers.Contains("Idempotency-Key"))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "idempotency-key-required" }) };
            }
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/admin/sites/{Site}/preview/realm")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { tenantId = TenantId, host = SiteHost }) };
            }
            var viewAs = Regex.Match(path, "^/api/admin/plugins/users/users/([0-9a-f-]{36})/preview-token$");
            if (request.Method == HttpMethod.Post && viewAs.Success)
            {
                var answer = await test.RealmAdminSendAsync(HttpMethod.Post, $"/users/{viewAs.Groups[1].Value}/preview-token",
                    new { requestedBy = Guid.Parse(test._ada) }, ct);
                return new HttpResponseMessage(answer.StatusCode) { Content = new StringContent(await answer.Content.ReadAsStringAsync(ct), System.Text.Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>One browser over both hosts: a shared cookie jar, redirects followed by hand.</summary>
    private sealed class Browser(HttpMessageHandler identity, HttpMessageHandler edge)
    {
        private readonly HttpClient _client = new(new CookieContainerHandler(new CookieContainer())
        {
            InnerHandler = new HostRouter(identity, edge),
        });

        public Task<HttpResponseMessage> SendRawAsync(Uri uri, CancellationToken ct) => _client.GetAsync(uri, ct);

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _client.SendAsync(request, ct);

        public Task<(Uri Uri, string Body)> GetAsync(Uri uri, CancellationToken ct)
            => FollowAsync(new HttpRequestMessage(HttpMethod.Get, uri), ct);

        public Task<(Uri Uri, string Body)> PostAsync(Uri uri, Dictionary<string, string> form, CancellationToken ct)
            => FollowAsync(new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) }, ct);

        private async Task<(Uri Uri, string Body)> FollowAsync(HttpRequestMessage request, CancellationToken ct)
        {
            for (var hop = 0; hop < 20; hop++)
            {
                var uri = request.RequestUri!;
                using var response = await _client.SendAsync(request, ct);
                if (response.Headers.Location is not { } location)
                {
                    return (uri, await response.Content.ReadAsStringAsync(ct));
                }
                request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, location));
            }
            throw new InvalidOperationException("Redirect loop.");
        }
    }

    private sealed class HostRouter(HttpMessageHandler identity, HttpMessageHandler edge) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _identity = new(identity);
        private readonly HttpMessageInvoker _edge = new(edge);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => (request.RequestUri!.Host == IdentityHost ? _identity : _edge).SendAsync(request, ct);
    }

    /// <summary>Vault's Transit stand-in: keys go to a test folder unencrypted.</summary>
    private sealed class NoTransit : ITransitEncryptor
    {
        public Task<string> EncryptAsync(string keyName, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
            => Task.FromResult(Convert.ToBase64String(plaintext.Span));

        public Task<byte[]> DecryptAsync(string keyName, string ciphertext, CancellationToken ct = default)
            => Task.FromResult(Convert.FromBase64String(ciphertext));
    }
}
