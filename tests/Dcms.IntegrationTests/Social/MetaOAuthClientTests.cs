extern alias AdminApiApp;
using AdminApiApp::Dcms.AdminApi.Social;
using Dcms.Shared.Data.Social;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dcms.IntegrationTests.Social;

/// <summary>
/// Exercises the Meta OAuth exchange against an in-process stub of the Graph API, the same
/// way the AI provider tests stub OpenAI. Runs locally — no Docker, no Meta app.
///
/// <para>The stub is the only way to test this at all: every one of these calls carries the
/// app secret and the tenant's token, so they can never be pointed at Meta from a test.</para>
/// </summary>
public class MetaOAuthClientTests
{
    /// <summary>Boots a stub Meta on a dynamic port and hands back a client aimed at it.</summary>
    private static async Task<(WebApplication Stub, MetaOAuthClient Client, List<string> Paths)> StubAsync(
        Action<WebApplication> routes, CancellationToken ct)
    {
        var paths = new List<string>();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.Use(async (http, next) =>
        {
            paths.Add(http.Request.Path.Value ?? string.Empty);
            await next();
        });
        routes(app);
        await app.StartAsync(ct);

        var options = Options.Create(new MetaSocialOptions
        {
            Meta = new MetaAppOptions { AppId = "fb-app", AppSecret = "fb-secret" },
            Instagram = new MetaAppOptions { AppId = "ig-app", AppSecret = "ig-secret" },
            RedirectUri = "https://admin.example.test/api/admin/social/callback",
            GraphVersion = "v25.0",
            OverrideBaseUrl = app.Urls.First(),
        });

        var client = new MetaOAuthClient(new HttpClient(), options, NullLogger<MetaOAuthClient>.Instance);
        return (app, client, paths);
    }

    [Fact]
    public async Task Facebook_code_exchange_upgrades_to_a_long_lived_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var (stub, client, paths) = await StubAsync(app =>
        {
            app.MapGet("/v25.0/oauth/access_token", (HttpContext http) =>
            {
                // The same path serves both calls; the grant_type is what distinguishes them.
                var isExchange = http.Request.Query["grant_type"] == "fb_exchange_token";
                return Results.Json(new
                {
                    access_token = isExchange ? "LONG-LIVED" : "SHORT-LIVED",
                    token_type = "bearer",
                    expires_in = isExchange ? 5_184_000 : 3600,
                });
            });
        }, ct);

        try
        {
            var token = await client.ExchangeCodeAsync(MetaProvider.Facebook, "the-code", ct);

            // The decisive assertion. A client that skipped the second call would return a
            // perfectly valid token that dies in an hour, and every test asserting only
            // "a token came back" would pass while production broke the next morning.
            token.AccessToken.Should().Be("LONG-LIVED");
            token.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(60), TimeSpan.FromMinutes(5));
            paths.Count(p => p == "/v25.0/oauth/access_token").Should().Be(2, "the code exchange and the long-lived upgrade are two separate calls");
        }
        finally
        {
            await stub.StopAsync(ct);
        }
    }

    [Fact]
    public async Task Facebook_discovery_returns_each_page_and_its_linked_instagram_account()
    {
        var ct = TestContext.Current.CancellationToken;
        var (stub, client, _) = await StubAsync(app =>
        {
            app.MapGet("/v25.0/me/accounts", () => Results.Json(new
            {
                data = new object[]
                {
                    new
                    {
                        id = "page-1",
                        name = "Acme Bakery",
                        access_token = "PAGE-1-TOKEN",
                        picture = new { data = new { url = "https://cdn.test/p1.jpg" } },
                        instagram_business_account = new
                        {
                            id = "ig-1", username = "acmebakery", name = "Acme Bakery",
                            profile_picture_url = "https://cdn.test/ig1.jpg",
                        },
                    },
                    // A Page with no linked Instagram account must not invent one.
                    new
                    {
                        id = "page-2",
                        name = "Acme Catering",
                        access_token = "PAGE-2-TOKEN",
                        picture = new { data = new { url = (string?)null } },
                        instagram_business_account = (object?)null,
                    },
                },
            }));
        }, ct);

        try
        {
            var accounts = await client.DiscoverAccountsAsync(MetaProvider.Facebook, "USER-TOKEN", ct);

            accounts.Should().HaveCount(3);

            var ig = accounts.Single(a => a.ExternalAccountId == "ig-1");
            ig.Username.Should().Be("acmebakery");
            // The linked Instagram account is read with the PAGE's token, not the user's.
            // Losing this is the bug that makes stories 400 with a confusing permissions error.
            ig.PageId.Should().Be("page-1");
            ig.PageToken.Should().Be("PAGE-1-TOKEN");

            accounts.Single(a => a.ExternalAccountId == "page-2").PageToken.Should().Be("PAGE-2-TOKEN");
            accounts.Should().NotContain(a => a.ExternalAccountId == "page-2" && a.Username != null);
        }
        finally
        {
            await stub.StopAsync(ct);
        }
    }

    [Fact]
    public async Task Instagram_login_exchange_posts_the_code_then_upgrades_on_the_graph_host()
    {
        var ct = TestContext.Current.CancellationToken;
        var (stub, client, paths) = await StubAsync(app =>
        {
            app.MapPost("/oauth/access_token", () => Results.Json(new
            {
                access_token = "IG-SHORT", user_id = "ig-user-1",
            }));
            app.MapGet("/access_token", () => Results.Json(new
            {
                access_token = "IG-LONG", token_type = "bearer", expires_in = 5_184_000,
            }));
        }, ct);

        try
        {
            var token = await client.ExchangeCodeAsync(MetaProvider.InstagramLogin, "the-code", ct);

            token.AccessToken.Should().Be("IG-LONG");
            // The two calls are a POST and a GET on different hosts in production; asserting
            // both were made is what catches a refactor that collapses them.
            paths.Should().Contain("/oauth/access_token").And.Contain("/access_token");
        }
        finally
        {
            await stub.StopAsync(ct);
        }
    }

    [Fact]
    public async Task A_dead_token_is_reported_as_needing_reauth_rather_than_as_a_transient_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var (stub, client, _) = await StubAsync(app =>
        {
            app.MapGet("/v25.0/oauth/access_token", () => Results.Json(new
            {
                error = new { message = "Session has expired", type = "OAuthException", code = 190 },
            }, statusCode: 400));
        }, ct);

        try
        {
            var act = async () => await client.RefreshAsync(MetaProvider.Facebook, "DEAD-TOKEN", ct);

            // Code 190 means "reconnect", not "retry later". Backoff would hide the problem
            // behind a slowly-growing retry interval and the feed would just quietly stop.
            var ex = await act.Should().ThrowAsync<MetaApiException>();
            ex.Which.IsTokenInvalid.Should().BeTrue();
        }
        finally
        {
            await stub.StopAsync(ct);
        }
    }

    /// <summary>
    /// The renewal both providers depend on. A Meta long-lived token lasts about sixty days
    /// and there is no refresh token — the exchange needs a token that still works — so a
    /// refresh that silently returns the same token, or hits the wrong host, is an outage
    /// scheduled two months out with nothing in the logs at the time.
    /// </summary>
    [Fact]
    public async Task Facebook_refresh_exchanges_the_current_token_for_a_later_expiry()
    {
        var ct = TestContext.Current.CancellationToken;
        var (stub, client, paths) = await StubAsync(app =>
        {
            app.MapGet("/v25.0/oauth/access_token", (HttpContext http) =>
            {
                http.Request.Query["grant_type"].ToString().Should().Be("fb_exchange_token");
                // The token being renewed has to be the one we sent, not the app secret or an
                // empty string — a URL built wrong still returns 200 from a lenient stub.
                http.Request.Query["fb_exchange_token"].ToString().Should().Be("OLD-TOKEN");
                return Results.Json(new
                {
                    access_token = "NEW-TOKEN", token_type = "bearer", expires_in = 5_184_000,
                });
            });
        }, ct);

        try
        {
            var token = await client.RefreshAsync(MetaProvider.Facebook, "OLD-TOKEN", ct);

            token.AccessToken.Should().Be("NEW-TOKEN");
            token.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddDays(50));
            paths.Should().ContainSingle().Which.Should().Be("/v25.0/oauth/access_token");
        }
        finally
        {
            await stub.StopAsync(ct);
        }
    }

    [Fact]
    public async Task Instagram_login_refresh_uses_its_own_endpoint_and_grant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (stub, client, paths) = await StubAsync(app =>
        {
            app.MapGet("/refresh_access_token", (HttpContext http) =>
            {
                // A different host, a different path and a different grant type from Facebook's.
                // Collapsing the two branches is the refactor this asserts against.
                http.Request.Query["grant_type"].ToString().Should().Be("ig_refresh_token");
                return Results.Json(new
                {
                    access_token = "IG-RENEWED", token_type = "bearer", expires_in = 5_184_000,
                });
            });
        }, ct);

        try
        {
            var token = await client.RefreshAsync(MetaProvider.InstagramLogin, "IG-OLD", ct);

            token.AccessToken.Should().Be("IG-RENEWED");
            paths.Should().ContainSingle().Which.Should().Be("/refresh_access_token");
        }
        finally
        {
            await stub.StopAsync(ct);
        }
    }
}
