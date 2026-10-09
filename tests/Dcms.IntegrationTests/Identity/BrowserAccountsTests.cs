extern alias IdentityApp;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IdentityApp::Dcms.Identity.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Dcms.IntegrationTests.Identity;

/// <summary>
/// Several platform accounts signed in on one browser, listed on the sign-in page and switched
/// between without a password while their logins are alive.
///
/// <para>Driven end to end like <see cref="LoginSessionRevocationTests"/>: a real login form, the
/// real chooser HTML, the real switch POST, and an authorization code exchanged for the ID token
/// a console would actually receive — because "switched" only means something if the next
/// authorization is issued for the other person.</para>
/// </summary>
public sealed class BrowserAccountsTests : IAsyncLifetime
{
    private const string Client = "dcms-edge";
    private const string Secret = "edge-test-secret";
    private const string RedirectUri = "https://admin.example.test/.edge/signin-oidc";
    private const string Scopes = "openid profile email roles dcms.admin offline_access";
    private const string Ada = "ada@example.test";
    private const string Grace = "grace@example.test";
    private const string Password = "Correct-Horse-Battery-9";

    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();
    private readonly string _keyRing = Path.Combine(Path.GetTempPath(), $"dcms-browser-accounts-{Guid.NewGuid():N}");
    private WebApplicationFactory<IdentityApp::Program> _factory = null!;
    private readonly Dictionary<string, Guid> _ids = [];

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new WebApplicationFactory<IdentityApp::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
            builder.UseSetting("Identity:Issuer", IdentityAppFixture.Issuer);
            builder.UseSetting("Identity:AllowInsecureHttp", "true");
            builder.UseSetting("Identity:Migrate", "true");
            builder.UseSetting("Identity:Seed", "true");
            builder.UseSetting("Identity:Edge:Secret", Secret);
            builder.UseSetting("Identity:Edge:RedirectUris", RedirectUri);
            builder.UseSetting("Identity:Edge:PostLogoutUris", "https://admin.example.test/.edge/signout-callback-oidc");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Nats:Url"] = "nats://localhost:4222" }));
            builder.ConfigureTestServices(services => services
                .AddDataProtection()
                .PersistKeysToFileSystem(Directory.CreateDirectory(_keyRing)));
        });
        using var _ = _factory.CreateClient();

        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<DcmsUser>>();
        foreach (var (email, name) in new[] { (Ada, "Ada"), (Grace, "Grace") })
        {
            var user = new DcmsUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = name };
            var created = await users.CreateAsync(user, Password);
            created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Description)));
            _ids[email] = user.Id;
        }
    }

    [DockerFact]
    public async Task Every_account_signed_in_on_a_browser_is_listed_as_logged_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = Browser();
        await SignInAsync(browser, Ada, ct);
        await SignInAsync(browser, Grace, ct);

        var page = await ChooserAsync(browser, ct);
        page.Should().ContainKey(Ada).WhoseValue.LoggedIn.Should().BeTrue();
        page.Should().ContainKey(Grace).WhoseValue.LoggedIn.Should().BeTrue();

        // A browser that never signed in has nothing to choose from: the plain form.
        var stranger = await Browser().GetStringAsync("/account/login", ct);
        stranger.Should().Contain("name=\"password\"").And.NotContain("Choose an account");
    }

    [DockerFact]
    public async Task Switching_signs_in_as_that_account_without_a_password()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = Browser();
        await SignInAsync(browser, Ada, ct);
        await SignInAsync(browser, Grace, ct); // Grace is now the signed-in account

        (await SubjectOfNextAuthorizationAsync(browser, ct)).Should().Be(_ids[Grace].ToString());

        var switched = await SwitchAsync(browser, (await ChooserAsync(browser, ct))[Ada].Id, ct);
        switched.Should().Be("/connect/authorize-resume", "a switch lands on the returnUrl it was given");

        (await SubjectOfNextAuthorizationAsync(browser, ct)).Should().Be(_ids[Ada].ToString(),
            "the next authorization is issued for the account that was picked");
    }

    [DockerFact]
    public async Task Signing_out_marks_only_that_account_signed_out_and_it_then_needs_a_password()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = Browser();
        await SignInAsync(browser, Ada, ct);
        await SignInAsync(browser, Grace, ct);

        using (var logout = await browser.GetAsync("/connect/logout", ct))
        {
            logout.StatusCode.Should().Be(HttpStatusCode.Found);
        }

        var page = await ChooserAsync(browser, ct);
        page[Grace].LoggedIn.Should().BeFalse("Grace was the one signed out");
        page[Ada].LoggedIn.Should().BeTrue("signing one account out leaves the others alone");

        // A signed-out row offers the form, not a switch; and a crafted switch to it is refused.
        (await SwitchAsync(browser, page[Grace].Id, ct)).Should().StartWith("/account/login");
        (await AuthorizeAsync(browser, ct)).Location.Should().StartWith("/account/login");
    }

    [DockerFact]
    public async Task Signing_in_again_to_a_live_account_resumes_its_login_rather_than_orphaning_it()
    {
        // A second password sign-in for the same account on the same browser must not mint a
        // new login over the live one: the console renewing from the first would keep running
        // on an id no row names, and "Remove from this browser" could never end it.
        var ct = TestContext.Current.CancellationToken;
        var browser = Browser();
        await SignInAsync(browser, Ada, ct);
        var first = await LoginIdOfNextAuthorizationAsync(browser, ct);

        await SignInAsync(browser, Ada, ct);
        (await LoginIdOfNextAuthorizationAsync(browser, ct)).Should().Be(first);
    }

    [DockerFact]
    public async Task Another_browser_cannot_switch_to_an_account_it_never_signed_in_with()
    {
        // The row id is in the page's HTML, so it is not a secret. The device cookie is, and the
        // switch is authorised on it alone.
        var ct = TestContext.Current.CancellationToken;
        var victim = Browser();
        await SignInAsync(victim, Ada, ct);
        var rowId = (await ChooserAsync(victim, ct))[Ada].Id;

        var attacker = Browser();
        await SignInAsync(attacker, Grace, ct);
        (await SwitchAsync(attacker, rowId, ct)).Should().StartWith("/account/login");
        (await SubjectOfNextAuthorizationAsync(attacker, ct)).Should().Be(_ids[Grace].ToString());
    }

    [DockerFact]
    public async Task Select_account_prompt_shows_the_chooser_and_resumes_the_same_authorization()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = Browser();
        await SignInAsync(browser, Ada, ct);

        var auth = await AuthorizeAsync(browser, ct, prompt: "select_account");
        auth.Location.Should().StartWith("/account/login?returnUrl=",
            "a signed-in browser is still shown the list when the console asked to switch");

        var resume = QueryHelpers.ParseQuery(new Uri("https://x" + auth.Location).Query)["returnUrl"].ToString();
        resume.Should().StartWith("/connect/authorize?").And.NotContain("prompt",
            "the resumed request must not ask for the chooser again, or picking would loop");
    }

    [DockerFact]
    public async Task Removing_an_account_ends_its_login()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = Browser();
        await SignInAsync(browser, Ada, ct);
        var auth = await AuthorizeAsync(browser, ct);
        var tokens = await ExchangeAsync(browser, CodeFrom(auth.Location), auth.Verifier, ct);

        var chooser = await browser.GetStringAsync("/account/login", ct);
        using var forget = await browser.PostAsync("/account/forget", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["id"] = Rows(chooser)[Ada].Id.ToString(),
                ["__RequestVerificationToken"] = CsrfToken(chooser),
            }), ct);
        forget.StatusCode.Should().Be(HttpStatusCode.Found);

        (await browser.GetStringAsync("/account/login", ct)).Should().NotContain(Ada);
        using var refresh = await RefreshAsync(tokens, ct);
        refresh.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "remove on a shared computer must not leave a console somewhere signed in as that person");
    }

    [DockerFact]
    public async Task A_password_change_shows_the_account_signed_out()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = Browser();
        await SignInAsync(browser, Ada, ct);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<DcmsUser>>();
            var ada = await users.FindByEmailAsync(Ada);
            (await users.ChangePasswordAsync(ada!, Password, Password + "-2")).Succeeded.Should().BeTrue();
        }

        (await ChooserAsync(browser, ct))[Ada].LoggedIn.Should().BeFalse(
            "a new password ends every login made with the old one, here as on the cookie");
    }

    [DockerFact]
    public async Task A_device_cookie_planted_by_a_sibling_host_is_never_used()
    {
        // Cookie tossing. A tenant page on *.dcms.highgeek.eu shares the registrable domain, so
        // it can set "dcms.device=<known>; Domain=highgeek.eu" in a visitor's browser. Had the
        // server adopted that value, the victim's sign-ins would be recorded under a device the
        // attacker holds — and the attacker's own browser could switch into them, no password.
        // Over HTTPS the real cookie is __Host- prefixed, which no sibling can set.
        var ct = TestContext.Current.CancellationToken;
        var https = new Uri("https://localhost");
        var victimJar = new CookieContainer();
        victimJar.Add(https, new Cookie("dcms.device", "planted-by-a-tenant-page"));
        var victim = _factory.CreateDefaultClient(https, new CookieContainerHandler(victimJar));
        await SignInAsync(victim, Ada, ct);

        var device = victimJar.GetCookies(https).Single(c => c.Name.StartsWith("__Host-", StringComparison.Ordinal));
        device.Name.Should().Be("__Host-dcms.device");
        device.Value.Should().NotBe("planted-by-a-tenant-page");
        device.Secure.Should().BeTrue();
        device.HttpOnly.Should().BeTrue();

        var attackerJar = new CookieContainer();
        attackerJar.Add(https, new Cookie("dcms.device", "planted-by-a-tenant-page"));
        var attacker = _factory.CreateDefaultClient(https, new CookieContainerHandler(attackerJar));
        (await attacker.GetStringAsync("/account/login", ct)).Should().NotContain("Choose an account",
            "the planted value names no browser the server ever recorded an account on");
    }

    private sealed record Row(Guid Id, bool LoggedIn);

    private HttpClient Browser() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task SignInAsync(HttpClient browser, string email, CancellationToken ct)
    {
        var page = await browser.GetStringAsync("/account/login?add=true", ct);
        using var response = await browser.PostAsync("/account/login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["email"] = email,
                ["password"] = Password,
                ["__RequestVerificationToken"] = CsrfToken(page),
            }), ct);
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().NotContain("error");
    }

    private static async Task<Dictionary<string, Row>> ChooserAsync(HttpClient browser, CancellationToken ct)
    {
        var page = await browser.GetStringAsync("/account/login", ct);
        page.Should().Contain("Choose an account");
        return Rows(page);
    }

    /// <summary>Each list item: the email it shows, its switch/forget id, and its status badge.</summary>
    private static Dictionary<string, Row> Rows(string page)
        => Regex.Matches(page, "<li>(.*?)</li>", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .ToDictionary(
                li => Regex.Match(li, "class=\"mail\">([^<]+)<").Groups[1].Value,
                li => new Row(
                    Guid.Parse(Regex.Match(li, "name=\"id\" value=\"([0-9a-f-]{36})\"").Groups[1].Value),
                    li.Contains("Logged in")));

    private static string CsrfToken(string page)
        => Regex.Match(page, """name="__RequestVerificationToken" value="([^"]+)""").Groups[1].Value;

    /// <summary>Posts the switch, returning where it redirected.</summary>
    private static async Task<string> SwitchAsync(HttpClient browser, Guid id, CancellationToken ct)
    {
        var page = await browser.GetStringAsync("/account/login", ct);
        using var response = await browser.PostAsync("/account/switch", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["id"] = id.ToString(),
                ["returnUrl"] = "/connect/authorize-resume",
                ["__RequestVerificationToken"] = CsrfToken(page),
            }), ct);
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        return response.Headers.Location!.ToString();
    }

    private async Task<string?> LoginIdOfNextAuthorizationAsync(HttpClient browser, CancellationToken ct)
    {
        var auth = await AuthorizeAsync(browser, ct);
        return IdTokenClaim(await ExchangeAsync(browser, CodeFrom(auth.Location), auth.Verifier, ct), "dcms_lsid");
    }

    private async Task<string?> SubjectOfNextAuthorizationAsync(HttpClient browser, CancellationToken ct)
    {
        var auth = await AuthorizeAsync(browser, ct);
        auth.Location.Should().StartWith(RedirectUri);
        var tokens = await ExchangeAsync(browser, CodeFrom(auth.Location), auth.Verifier, ct);
        return IdTokenClaim(tokens, "sub");
    }

    private static async Task<(string Location, string Verifier)> AuthorizeAsync(
        HttpClient browser, CancellationToken ct, string? prompt = null)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = Client,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = Scopes,
            ["state"] = "state-1",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        if (prompt is not null)
        {
            query["prompt"] = prompt;
        }

        using var response = await browser.GetAsync(QueryHelpers.AddQueryString("/connect/authorize", query), ct);
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        return (response.Headers.Location!.ToString(), verifier);
    }

    private async Task<JsonElement> ExchangeAsync(
        HttpClient browser, string code, string verifier, CancellationToken ct)
    {
        using var response = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = Client,
                ["client_secret"] = Secret,
                ["code_verifier"] = verifier,
            }), ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private Task<HttpResponseMessage> RefreshAsync(JsonElement tokens, CancellationToken ct)
        => _factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
                ["client_id"] = Client,
                ["client_secret"] = Secret,
            }), ct);

    private static string CodeFrom(string location)
        => QueryHelpers.ParseQuery(new Uri(location).Query)["code"].ToString();

    private static string? IdTokenClaim(JsonElement tokens, string name)
    {
        var payload = tokens.GetProperty("id_token").GetString()!.Split('.')[1];
        var padded = payload.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        var claims = JsonDocument.Parse(Convert.FromBase64String(padded)).RootElement;
        return claims.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
        await _postgres.DisposeAsync();
        if (Directory.Exists(_keyRing))
        {
            Directory.Delete(_keyRing, recursive: true);
        }
    }
}
