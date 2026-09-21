extern alias IdentityApp;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace Dcms.IntegrationTests.Identity;

/// <summary>
/// ADR 0014 phase 2: whether the edge may ask for what its BFF session needs.
///
/// <para><b>This is the one thing that flip can break, and it breaks wide.</b> Turning
/// <c>Edge:Auth:Bff</c> on adds <c>dcms.admin</c> and <c>offline_access</c> to the scopes the
/// edge requests at sign-in. OpenIddict validates requested scopes against the client's
/// permissions at the authorization endpoint, before it ever challenges for a login — so a
/// scope the <c>dcms-edge</c> client does not hold is not a degraded BFF, it is a refused
/// sign-in. Grafana and Forgejo share that client, so the symptom is every operator locked out
/// of the dashboards.</para>
///
/// <para>And it is refused <i>loudly</i>: OpenIddict answers <c>400</c> with
/// <c>error:invalid_request</c> and "This client application is not allowed to use the
/// specified scope" (ID2051) rather than redirecting the error back to the client. So the
/// failure would be a bare error page on the console's own host, not a redirect loop — worth
/// knowing before it is seen rather than after.</para>
///
/// <para>Unauthenticated on purpose: the refusal happens before authentication, which is what
/// makes this assertable without driving a login form.</para>
/// </summary>
public sealed class EdgeClientAuthorizationTests : IAsyncLifetime
{
    private const string RedirectUri = "https://admin.example.test/.edge/signin-oidc";

    /// <summary>What EdgeAuthentication asks for once Edge:Auth:Bff is on.</summary>
    private const string BffScopes = "openid profile email roles dcms.admin offline_access";

    /// <summary>What it asks for today, with the flag off.</summary>
    private const string CurrentScopes = "openid profile email roles";

    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();
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
            // Without a secret the edge client is not seeded at all, deliberately, so an
            // installation with no edge auth does not carry a dead client.
            builder.UseSetting("Identity:Edge:Secret", "edge-test-secret");
            builder.UseSetting("Identity:Edge:RedirectUris", RedirectUri);
            builder.UseSetting("Identity:Edge:PostLogoutUris", "https://admin.example.test/.edge/signout-callback-oidc");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Nats:Url"] = "nats://localhost:4222" }));
        });
        using var _ = _factory.CreateClient();
    }

    [DockerFact]
    public async Task The_edge_may_request_the_scopes_its_bff_session_needs()
    {
        var outcome = await AuthorizeAsync(BffScopes);

        // Reaching the login page is as far as an unauthenticated request can get, and it is
        // past the scope check.
        outcome.Error.Should().BeNull(
            "Edge:Auth:Bff=true makes the edge ask for exactly these, and a scope the client "
            + "does not hold refuses the sign-in Grafana and Forgejo share");
        outcome.Location.Should().StartWith("/account/login");
    }

    [DockerFact]
    public async Task The_scopes_it_asks_for_today_keep_working()
    {
        // The flag-off path, so this file also fails if converging the client's scope
        // permissions took away something it already had.
        var outcome = await AuthorizeAsync(CurrentScopes);

        outcome.Error.Should().BeNull();
        outcome.Location.Should().StartWith("/account/login");
    }

    [DockerFact]
    public async Task A_scope_the_edge_client_does_not_hold_is_refused()
    {
        // The control. Without it the two assertions above would pass just as happily against
        // an identity server that never validates scopes at all. dcms.platform belongs to the
        // platform console, and the edge has no business asking for it.
        var outcome = await AuthorizeAsync($"{CurrentScopes} dcms.platform");

        outcome.Status.Should().Be(HttpStatusCode.BadRequest);
        outcome.Error.Should().Be("invalid_request");
        outcome.Description.Should().Contain("not allowed to use the specified scope");
    }

    /// <summary>
    /// What the authorization endpoint does with a request: redirect to the login page, or
    /// refuse it.
    ///
    /// <para>Auto-redirect is off so the 302 is readable. A refusal for a scope the client may
    /// not use is <b>not</b> redirected back to the client — OpenIddict answers 400 with a text
    /// body of <c>key:value</c> lines — so both shapes are handled here rather than assumed.</para>
    /// </summary>
    private async Task<AuthorizeOutcome> AuthorizeAsync(string scope)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var url = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = "dcms-edge",
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["state"] = "state-1",
            // PKCE is required of this client, and a request missing it is refused for that
            // reason instead — which would make every case here look alike.
            ["code_challenge"] = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            ["code_challenge_method"] = "S256",
        });

        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        var location = response.Headers.Location?.ToString();

        if (response.StatusCode == HttpStatusCode.Found)
        {
            // An error that CAN be redirected comes back on the redirect URI's query.
            var query = location!.Contains('?')
                ? QueryHelpers.ParseQuery(location[(location.IndexOf('?') + 1)..])
                : [];
            return new AuthorizeOutcome(
                response.StatusCode,
                location,
                query.TryGetValue("error", out var redirected) ? redirected.ToString() : null,
                query.TryGetValue("error_description", out var reason) ? reason.ToString() : null);
        }

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var fields = body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);

        return new AuthorizeOutcome(
            response.StatusCode,
            location,
            fields.GetValueOrDefault("error"),
            fields.GetValueOrDefault("error_description"));
    }

    private sealed record AuthorizeOutcome(
        HttpStatusCode Status, string? Location, string? Error, string? Description);

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
        await _postgres.DisposeAsync();
    }
}
