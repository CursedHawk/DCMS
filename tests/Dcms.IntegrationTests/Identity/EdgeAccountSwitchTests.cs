extern alias EdgeApp;
extern alias IdentityApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Dcms.Shared.Vault;
using EdgeApp::Dcms.Edge;
using EdgeApp::Dcms.Edge.Auth;
using IdentityApp::Dcms.Identity.Domain;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Dcms.IntegrationTests.Identity;

/// <summary>
/// Switching accounts through the real edge: its OIDC handler, cookie, BFF session store and
/// login check, signed in against a real identity, in one browser that carries both hosts'
/// cookies.
///
/// <para>Why this exists: identity's half of a switch had tests, the edge's half had none, and
/// the edge's half was broken twice over. The OIDC callback runs before the request's cookie is
/// authenticated, so "replace the previous session" read an empty <c>HttpContext.User</c> and
/// every switch left one more device on the account page; and the admin console asked about
/// its session from a 15-second cache, so it lagged a switch the platform console followed at
/// once.</para>
/// </summary>
public sealed class EdgeAccountSwitchTests : IAsyncLifetime
{
    private const string Secret = "edge-test-secret";
    private const string AdminHost = "admin.example.test";
    private const string IdentityHost = "identity.test";
    private const string Ada = "ada@example.test";
    private const string Grace = "grace@example.test";
    private const string Password = "Correct-Horse-Battery-9";

    private static readonly Uri Admin = new($"https://{AdminHost}/");
    private static readonly Uri Auth = new($"http://{IdentityHost}/");

    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();
    private readonly string _keyRing = Path.Combine(Path.GetTempPath(), $"dcms-edge-switch-{Guid.NewGuid():N}");
    private WebApplicationFactory<IdentityApp::Program> _identity = null!;
    private ConnectionMultiplexer _connection = null!;
    private WebApplication _edge = null!;
    private readonly Dictionary<string, string> _subjects = [];

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
            foreach (var email in new[] { Ada, Grace })
            {
                var user = new DcmsUser { UserName = email, Email = email, EmailConfirmed = true };
                (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();
                _subjects[email] = user.Id.ToString();
            }
        }

        _connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        _edge = BuildEdge();
        await _edge.StartAsync();
    }

    /// <summary>The edge's own authentication wiring, as Program.cs composes it, minus what a test
    /// cannot have: Vault (keys go to a folder) and the network (both backchannels are the
    /// identity test server).</summary>
    private WebApplication BuildEdge()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Edge:AdminHost"] = AdminHost,
            ["Edge:Auth:Authority"] = IdentityAppFixture.Issuer,
            ["Edge:Auth:InternalAuthority"] = $"http://{IdentityHost}",
            ["Edge:Auth:ClientSecret"] = Secret,
            ["Edge:Auth:Bff"] = "true",
        });
        builder.Services.Configure<EdgeOptions>(builder.Configuration.GetSection(EdgeOptions.SectionName));
        builder.Services.Configure<EdgeAuthOptions>(builder.Configuration.GetSection(EdgeAuthOptions.SectionName));
        builder.Services.AddSingleton<IConnectionMultiplexer>(_connection);
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<ITransitEncryptor, NoTransit>();
        builder.AddEdgeAuthentication();
        builder.AddBff();

        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(Directory.CreateDirectory(Path.Combine(_keyRing, "edge")));
        builder.Services.Configure<KeyManagementOptions>(o => o.XmlEncryptor = null);
        builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
        {
            o.Backchannel = new HttpClient(_identity.Server.CreateHandler());
            o.RequireHttpsMetadata = false;
        });
        builder.Services.AddHttpClient<LoginSessionCheck>()
            .ConfigurePrimaryHttpMessageHandler(() => _identity.Server.CreateHandler());

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapEdgeAuthEndpoints();
        app.MapBffEndpoints();
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
    public async Task Switching_in_the_console_replaces_its_session_rather_than_adding_a_device()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = new Browser(_identity.Server.CreateHandler(), _edge.GetTestServer().CreateHandler());
        var store = _edge.Services.GetRequiredService<BffSessionStore>();

        await SignInWithPasswordAsync(browser, "/.edge/signin?returnUrl=/", Ada, ct);
        (await MeAsync(browser, ct)).Should().Be(_subjects[Ada]);
        (await store.ListAsync(_subjects[Ada])).Should().ContainSingle();

        // "Switch account" → "Use another account" → Grace.
        await SignInWithPasswordAsync(browser, "/.edge/signin?prompt=select_account&returnUrl=/", Grace, ct);
        (await MeAsync(browser, ct)).Should().Be(_subjects[Grace]);
        (await store.ListAsync(_subjects[Ada])).Should().BeEmpty("the browser's Ada session was replaced, not kept");
        (await store.ListAsync(_subjects[Grace])).Should().ContainSingle();

        // Back to Ada from the chooser, then Ada again: still one device each way.
        await SwitchAsync(browser, Ada, ct);
        await SwitchAsync(browser, Ada, ct);
        (await MeAsync(browser, ct)).Should().Be(_subjects[Ada]);
        (await store.ListAsync(_subjects[Ada])).Should().ContainSingle("the same browser is one device, however often it switches");
        (await store.ListAsync(_subjects[Grace])).Should().BeEmpty();
    }

    [DockerFact]
    public async Task The_console_follows_a_switch_made_elsewhere_at_once_and_signs_in_as_that_account()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = new Browser(_identity.Server.CreateHandler(), _edge.GetTestServer().CreateHandler());
        var store = _edge.Services.GetRequiredService<BffSessionStore>();
        await SignInWithPasswordAsync(browser, "/.edge/signin?returnUrl=/", Ada, ct);
        (await MeAsync(browser, ct)).Should().Be(_subjects[Ada]); // the check's answer is now cached

        // The platform console (or the sign-in page) picks Grace: identity only, the edge
        // hears nothing.
        var login = await browser.GetAsync(new Uri(Auth, "/account/login?add=true"), ct);
        await browser.PostAsync(new Uri(Auth, "/account/login"), new Dictionary<string, string>
        {
            ["email"] = Grace,
            ["password"] = Password,
            ["__RequestVerificationToken"] = CsrfToken(login.Body),
        }, ct);

        // Within the cache's lifetime, and still told — and told to sign in again, not out.
        using (var me = await browser.SendRawAsync(new Uri(Admin, "/.edge/me"), ct))
        {
            me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            me.Headers.GetValues(LoginSessionCheck.SwitchedHeader).Should().Equal(LoginSessionCheck.SwitchedValue);
        }
        (await store.ListAsync(_subjects[Ada])).Should().BeEmpty("the switched-away session is ended, not left listed");

        // Which completes without a prompt, as Grace.
        var landed = await browser.GetAsync(new Uri(Admin, "/.edge/signin?returnUrl=/"), ct);
        landed.Uri.Host.Should().Be(AdminHost, "identity signs the console in silently as the browser's account");
        (await MeAsync(browser, ct)).Should().Be(_subjects[Grace]);
    }

    private static async Task SignInWithPasswordAsync(Browser browser, string start, string email, CancellationToken ct)
    {
        var page = await browser.GetAsync(new Uri(Admin, start), ct);
        page.Uri.AbsolutePath.Should().Be("/account/login");
        var landed = await browser.PostAsync(new Uri(Auth, "/account/login"), new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = Password,
            ["returnUrl"] = QueryHelpers.ParseQuery(page.Uri.Query)["returnUrl"].ToString(),
            ["__RequestVerificationToken"] = CsrfToken(page.Body),
        }, ct);
        landed.Uri.Host.Should().Be(AdminHost, landed.Body);
    }

    private static async Task SwitchAsync(Browser browser, string email, CancellationToken ct)
    {
        var page = await browser.GetAsync(new Uri(Admin, "/.edge/signin?prompt=select_account&returnUrl=/"), ct);
        page.Body.Should().Contain("Choose an account");
        var item = Regex.Matches(page.Body, "<li>(.*?)</li>", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Single(li => li.Contains($"class=\"mail\">{email}<"));
        var landed = await browser.PostAsync(new Uri(Auth, "/account/switch"), new Dictionary<string, string>
        {
            ["id"] = Regex.Match(item, "name=\"id\" value=\"([0-9a-f-]{36})\"").Groups[1].Value,
            ["returnUrl"] = QueryHelpers.ParseQuery(page.Uri.Query)["returnUrl"].ToString(),
            ["__RequestVerificationToken"] = CsrfToken(page.Body),
        }, ct);
        landed.Uri.Host.Should().Be(AdminHost, landed.Body);
    }

    /// <summary>The subject the admin console is signed in as, or null.</summary>
    private static async Task<string?> MeAsync(Browser browser, CancellationToken ct)
    {
        using var response = await browser.SendRawAsync(new Uri(Admin, "/.edge/me"), ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
        return body.GetProperty("sub").GetString();
    }

    private static string CsrfToken(string page)
        => Regex.Match(page, """name="__RequestVerificationToken" value="([^"]+)""").Groups[1].Value;

    /// <summary>One browser over both hosts: a shared cookie jar, redirects followed by hand.</summary>
    private sealed class Browser
    {
        private readonly HttpClient _client;

        public Browser(HttpMessageHandler identity, HttpMessageHandler edge)
        {
            _client = new HttpClient(new CookieContainerHandler(new CookieContainer())
            {
                InnerHandler = new HostRouter(identity, edge),
            });
        }

        public Task<HttpResponseMessage> SendRawAsync(Uri uri, CancellationToken ct)
            => _client.GetAsync(uri, ct);

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
