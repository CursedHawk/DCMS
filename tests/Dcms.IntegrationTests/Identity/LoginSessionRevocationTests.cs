extern alias IdentityApp;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IdentityApp::Dcms.Identity.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Dcms.IntegrationTests.Identity;

/// <summary>
/// "Sign out that device" has to reach the login, not only the session.
///
/// <para><b>The bug this exists for.</b> Ending an edge BFF session deletes the console's
/// tokens and the console falls back to its sign-in screen — which looks exactly like a
/// successful sign-out. It is not: identity's own cookie is still in that browser, so pressing
/// "Sign in" completes <c>/connect/authorize</c> with no prompt and the device is back inside
/// one redirect. The device that was reported signed out never was, and the failure is
/// invisible from the device that did the revoking.</para>
///
/// <para><b>And the cookie is the part that got this wrong.</b> The first attempt minted the id
/// when the user signed in, which leaves every browser already holding a cookie without one —
/// forever, since nothing signs them in again. It worked in a private window and nowhere else.
/// So these tests deliberately never re-enter a password between the sign-in and the
/// authorization: the cookie arrives at <c>/connect/authorize</c> exactly as an old one does,
/// and the id has to come from there.</para>
///
/// <para>So this drives the whole chain rather than any one part of it: a real interactive
/// login, a real authorization code exchanged for real tokens, the id read out of the ID token
/// the client actually receives, the revoke endpoint called with that client's own bearer, and
/// then the same authorize request again. Every link is somewhere the id could be dropped, and
/// dropping it anywhere produces the identical symptom — a sign-out that silently is not one.</para>
/// </summary>
public sealed class LoginSessionRevocationTests : IAsyncLifetime
{
    private const string Client = "dcms-edge";
    private const string Secret = "edge-test-secret";
    private const string RedirectUri = "https://admin.example.test/.edge/signin-oidc";
    private const string Scopes = "openid profile email roles dcms.admin offline_access";
    private const string Email = "ada@example.test";
    private const string Password = "Correct-Horse-Battery-9";

    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();
    private readonly string _keyRing = Path.Combine(Path.GetTempPath(), $"dcms-login-sessions-{Guid.NewGuid():N}");
    private WebApplicationFactory<IdentityApp::Program> _factory = null!;

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
            // The production key ring lives in a schema owned by a migration this fixture does
            // not run, and the first antiforgery token would 500 on the missing table. Which
            // store protects the cookies is not what is under test; that there IS one is, so a
            // throwaway directory per run is enough.
            builder.ConfigureTestServices(services => services
                .AddDataProtection()
                .PersistKeysToFileSystem(Directory.CreateDirectory(_keyRing)));
        });
        using var _ = _factory.CreateClient();

        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<DcmsUser>>();
        var created = await users.CreateAsync(
            new DcmsUser { UserName = Email, Email = Email, EmailConfirmed = true, DisplayName = "Ada" },
            Password);
        created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Description)));
    }

    [DockerFact]
    public async Task A_revoked_login_cannot_silently_authorize_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = Browser();
        await SignInAsync(browser, ct);

        // Before: the cookie is enough, which is the behaviour the console depends on — this is
        // why pressing "Sign in" after a revoke used to be instant.
        var first = await AuthorizeAsync(browser, ct);
        first.Location.Should().StartWith(RedirectUri, "a live login authorizes with no prompt");

        var tokens = await ExchangeAsync(browser, CodeFrom(first.Location), first.Verifier, ct);
        var loginSessionId = IdTokenClaim(tokens, "dcms_lsid");
        loginSessionId.Should().NotBeNullOrEmpty(
            "the edge records this at sign-in and has nothing to revoke without it — and the "
            + "failure is a sign-out that appears to work");

        // The edge's call, made exactly as the edge makes it: the session's own access token.
        var revoke = new HttpRequestMessage(HttpMethod.Delete, $"/account/api/sessions/{Uri.EscapeDataString(loginSessionId!)}");
        revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken(tokens));
        using var revoked = await _factory.CreateClient().SendAsync(revoke, ct);
        revoked.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // After: the same browser, the same cookie, the same request.
        var second = await AuthorizeAsync(browser, ct);
        second.Location.Should().StartWith("/account/login",
            "the login behind the session is ended, so the cookie no longer authorizes and the "
            + "device has to present credentials again");
    }

    [DockerFact]
    public async Task One_browser_keeps_one_login_id_across_authorizations()
    {
        // The id has to be STABLE, not merely present. If each authorization minted a fresh one
        // the list would still show a session and revoking it would still answer 204 — while
        // revoking an id the login had already stopped using. That is the identical symptom as
        // having no id at all, one layer further down, and nothing about it is visible from
        // either device.
        var ct = TestContext.Current.CancellationToken;
        var browser = Browser();
        await SignInAsync(browser, ct);

        var first = await AuthorizeAsync(browser, ct);
        var second = await AuthorizeAsync(browser, ct);

        var firstId = IdTokenClaim(await ExchangeAsync(browser, CodeFrom(first.Location), first.Verifier, ct), "dcms_lsid");
        var secondId = IdTokenClaim(await ExchangeAsync(browser, CodeFrom(second.Location), second.Verifier, ct), "dcms_lsid");

        firstId.Should().NotBeNullOrEmpty();
        secondId.Should().Be(firstId, "the id is minted once and written back to the cookie");
    }

    [DockerFact]
    public async Task Ending_one_login_leaves_the_others_signed_in()
    {
        // The whole reason this is not the security stamp: rotating that would sign every device
        // out, and "sign out my other laptop" would end the session doing the asking.
        var ct = TestContext.Current.CancellationToken;
        var laptop = Browser();
        var phone = Browser();
        await SignInAsync(laptop, ct);
        await SignInAsync(phone, ct);

        var laptopAuth = await AuthorizeAsync(laptop, ct);
        var laptopTokens = await ExchangeAsync(laptop, CodeFrom(laptopAuth.Location), laptopAuth.Verifier, ct);
        var phoneAuth = await AuthorizeAsync(phone, ct);
        var phoneTokens = await ExchangeAsync(phone, CodeFrom(phoneAuth.Location), phoneAuth.Verifier, ct);

        IdTokenClaim(laptopTokens, "dcms_lsid").Should().NotBe(IdTokenClaim(phoneTokens, "dcms_lsid"),
            "two sign-ins in two browsers are two logins; one id for both would make every "
            + "revocation a sign-out of everything");

        var revoke = new HttpRequestMessage(
            HttpMethod.Delete, $"/account/api/sessions/{Uri.EscapeDataString(IdTokenClaim(phoneTokens, "dcms_lsid")!)}");
        revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken(laptopTokens));
        (await _factory.CreateClient().SendAsync(revoke, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await AuthorizeAsync(phone, ct)).Location.Should().StartWith("/account/login");
        (await AuthorizeAsync(laptop, ct)).Location.Should()
            .StartWith(RedirectUri, "the device doing the asking stays signed in");
    }

    [DockerFact]
    public async Task A_login_id_belonging_to_somebody_else_is_refused()
    {
        // The ownership check, which is the whole authorisation on this endpoint: the id carries
        // its owner's subject, so a caller naming another subject's login is refused without
        // identity having to store live logins to look it up.
        var ct = TestContext.Current.CancellationToken;
        var browser = Browser();
        await SignInAsync(browser, ct);
        var auth = await AuthorizeAsync(browser, ct);
        var tokens = await ExchangeAsync(browser, CodeFrom(auth.Location), auth.Verifier, ct);

        var stranger = $"{Guid.NewGuid()}.{Guid.NewGuid():N}";
        var revoke = new HttpRequestMessage(HttpMethod.Delete, $"/account/api/sessions/{Uri.EscapeDataString(stranger)}");
        revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken(tokens));

        using var response = await _factory.CreateClient().SendAsync(revoke, ct);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>A client that keeps its cookies, and does not chase redirects — both of which
    /// are what make it stand in for one browser.</summary>
    private HttpClient Browser() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task SignInAsync(HttpClient browser, CancellationToken ct)
    {
        using var pageResponse = await browser.GetAsync("/account/login", ct);
        var page = await pageResponse.Content.ReadAsStringAsync(ct);
        pageResponse.StatusCode.Should().Be(HttpStatusCode.OK, page);
        var token = Regex.Match(page, """name="__RequestVerificationToken" value="([^"]+)""").Groups[1].Value;
        token.Should().NotBeEmpty("the form is antiforgery-protected and the POST is refused without it");

        using var response = await browser.PostAsync("/account/login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["email"] = Email,
                ["password"] = Password,
                ["__RequestVerificationToken"] = token,
            }), ct);

        // A failed sign-in re-renders the form with 200; the redirect is the success.
        response.StatusCode.Should().Be(HttpStatusCode.Found);
    }

    /// <summary>The authorize request, answered either with the redirect back to the client or
    /// with the bounce to the login form. Returns whichever.</summary>
    private static async Task<(string Location, string Verifier)> AuthorizeAsync(
        HttpClient browser, CancellationToken ct)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var url = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = Client,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = Scopes,
            ["state"] = "state-1",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        });

        using var response = await browser.GetAsync(url, ct);
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

    private static string CodeFrom(string location)
        => QueryHelpers.ParseQuery(new Uri(location).Query)["code"].ToString();

    private static string AccessToken(JsonElement tokens) => tokens.GetProperty("access_token").GetString()!;

    /// <summary>
    /// A claim out of the ID token — the token the client actually receives, rather than the
    /// principal identity happens to hold. In the code flow the ID token is minted at the TOKEN
    /// endpoint, so a claim added only at the authorize endpoint never reaches the client; that
    /// is the mistake this reads through instead of around.
    /// </summary>
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
