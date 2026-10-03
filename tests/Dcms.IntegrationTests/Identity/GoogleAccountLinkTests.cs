extern alias IdentityApp;

using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using IdentityApp::Dcms.Identity.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Dcms.IntegrationTests.Identity;

/// <summary>
/// Google sign-in for an email that already has an account offers to connect Google to it,
/// and only does so with that account's password — a matching email is not proof of
/// ownership, since sign-up never confirms the address.
///
/// <para>Google itself is not under test: a test-only route deposits the external cookie
/// exactly where the Google handler would, and the flow starts at the callback.</para>
/// </summary>
public sealed class GoogleAccountLinkTests : IAsyncLifetime
{
    private const string Email = "ada@example.test";
    private const string Password = "Correct-Horse-Battery-9";
    private const string GoogleKey = "google-sub-1234";

    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();
    private readonly string _keyRing = Path.Combine(Path.GetTempPath(), $"dcms-google-link-{Guid.NewGuid():N}");
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
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Nats:Url"] = "nats://localhost:4222" }));
            builder.ConfigureTestServices(services =>
            {
                services.AddDataProtection().PersistKeysToFileSystem(Directory.CreateDirectory(_keyRing));
                services.AddSingleton<IStartupFilter, FakeGoogleReturn>();
            });
        });
        using var _ = _factory.CreateClient();

        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<DcmsUser>>();
        var created = await users.CreateAsync(
            new DcmsUser { UserName = Email, Email = Email, DisplayName = "Ada" }, Password);
        created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Description)));
    }

    [DockerFact]
    public async Task Existing_email_is_offered_a_link_that_needs_the_password()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        (await browser.GetAsync("/test/google-returned", ct)).EnsureSuccessStatusCode();
        using var prompt = await browser.GetAsync("/account/external/callback?returnUrl=/sites", ct);
        var page = await prompt.Content.ReadAsStringAsync(ct);
        page.Should().Contain("Connect Google?", "an existing account must not be sent to the create-account form");

        using var wrong = await LinkAsync(browser, page, "not-the-password", ct);
        wrong.StatusCode.Should().Be(HttpStatusCode.OK);
        var retry = await wrong.Content.ReadAsStringAsync(ct);
        retry.Should().Contain("Incorrect password.");
        (await GoogleLoginsAsync()).Should().BeEmpty("a wrong password must not link anything");

        using var right = await LinkAsync(browser, retry, Password, ct);
        right.StatusCode.Should().Be(HttpStatusCode.Found);
        right.Headers.Location!.OriginalString.Should().Be("/sites");
        (await GoogleLoginsAsync()).Should().ContainSingle(l => l.ProviderKey == GoogleKey);

        // And from now on Google signs straight in.
        var later = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        (await later.GetAsync("/test/google-returned", ct)).EnsureSuccessStatusCode();
        using var signIn = await later.GetAsync("/account/external/callback?returnUrl=/sites", ct);
        signIn.StatusCode.Should().Be(HttpStatusCode.Found);
        signIn.Headers.Location!.OriginalString.Should().Be("/sites");
    }

    private static Task<HttpResponseMessage> LinkAsync(HttpClient browser, string page, string password, CancellationToken ct)
    {
        var token = Regex.Match(page, """name="__RequestVerificationToken" value="([^"]+)""").Groups[1].Value;
        return browser.PostAsync("/account/external/link", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["password"] = password,
            ["returnUrl"] = "/sites",
            ["__RequestVerificationToken"] = token,
        }), ct);
    }

    private async Task<IList<UserLoginInfo>> GoogleLoginsAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<DcmsUser>>();
        return await users.GetLoginsAsync((await users.FindByEmailAsync(Email))!);
    }

    // What the Google handler leaves behind after a successful challenge: the external cookie
    // holding the provider's subject and the verified email, tagged with the provider name.
    private sealed class FakeGoogleReturn : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // Not app.Map: that moves the prefix into PathBase and the cookie gets scoped to it.
            app.Use(async (http, nextMiddleware) =>
            {
                if (http.Request.Path != "/test/google-returned")
                {
                    await nextMiddleware();
                    return;
                }
                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, GoogleKey), new Claim(ClaimTypes.Email, Email)], "Google"));
                var props = new AuthenticationProperties();
                props.Items["LoginProvider"] = "Google";
                await http.SignInAsync(IdentityConstants.ExternalScheme, principal, props);
            });
            next(app);
        };
    }

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
