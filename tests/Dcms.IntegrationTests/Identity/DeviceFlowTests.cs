extern alias IdentityApp;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IdentityApp::Dcms.Identity.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Dcms.IntegrationTests.Identity;

/// <summary>
/// The <c>dcms</c> command line's sign-in (ADR 0031): the device authorization grant. It shows a
/// code, the person approves it in a browser where they are signed in, and it gets their token.
/// The account page lists it and signs it out.
/// </summary>
public sealed class DeviceFlowTests : IAsyncLifetime
{
    private const string Client = "dcms-cli";
    private const string Email = "ada@example.test";
    private const string Password = "Correct-Horse-Battery-9";
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();
    private readonly string _keyRing = Path.Combine(Path.GetTempPath(), $"dcms-device-flow-{Guid.NewGuid():N}");
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
    public async Task A_command_line_signs_in_when_its_code_is_approved_and_out_from_the_account_page()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = await StartAsync(ct);

        (await PollAsync(device, ct)).error.Should().Be("authorization_pending");

        var browser = Browser();
        using (var anonymous = await browser.GetAsync($"/connect/verify?user_code={device.UserCode}", ct))
        {
            anonymous.StatusCode.Should().Be(HttpStatusCode.Redirect);
            anonymous.Headers.Location!.ToString().Should().StartWith("/account/login");
        }

        await SignInAsync(browser, ct);
        var page = await browser.GetStringAsync($"/connect/verify?user_code={Uri.EscapeDataString(device.UserCode)}", ct);
        page.Should().Contain("Approve").And.Contain(Email);
        using (var approved = await DecideAsync(browser, page, device.UserCode, "approve", ct))
        {
            approved.StatusCode.Should().Be(HttpStatusCode.Redirect);
            approved.Headers.Location!.ToString().Should().Contain("done=1");
        }

        var (tokens, error) = await PollAsync(device, ct);
        error.Should().BeNull();
        var access = tokens.GetProperty("access_token").GetString()!;
        var refresh = tokens.GetProperty("refresh_token").GetString()!;
        Claim(access, "client_id").Should().Be(Client, "the edge passes a bearer through only for this client");
        Claim(access, "scope").Should().Contain("dcms.admin");

        var api = _factory.CreateClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var devices = await api.GetFromJsonAsync<JsonElement>("/account/api/devices", ct);
        devices.GetArrayLength().Should().Be(1);
        var id = devices[0].GetProperty("id").GetString()!;

        (await api.DeleteAsync($"/account/api/devices/{id}", ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.GetFromJsonAsync<JsonElement>("/account/api/devices", ct)).GetArrayLength().Should().Be(0);

        using var renewed = await _factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = Client }), ct);
        renewed.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a signed-out command line cannot renew its token");
    }

    [DockerFact]
    public async Task A_denied_code_gets_no_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = await StartAsync(ct);
        var browser = Browser();
        await SignInAsync(browser, ct);
        var page = await browser.GetStringAsync($"/connect/verify?user_code={Uri.EscapeDataString(device.UserCode)}", ct);
        using (var denied = await DecideAsync(browser, page, device.UserCode, "deny", ct))
        {
            denied.Headers.Location!.ToString().Should().Contain("denied=1");
        }
        (await PollAsync(device, ct)).error.Should().Be("access_denied");
    }

    [DockerFact]
    public async Task A_wrong_code_is_said_so()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = Browser();
        await SignInAsync(browser, ct);
        var page = await browser.GetStringAsync("/connect/verify?user_code=NOPE-NOPE", ct);
        page.Should().Contain("not valid").And.NotContain("Approve");
    }

    private sealed record Device(string DeviceCode, string UserCode);

    private async Task<Device> StartAsync(CancellationToken ct)
    {
        using var response = await _factory.CreateClient().PostAsync("/connect/device", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["client_id"] = Client, ["scope"] = "openid offline_access dcms.admin" }), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("verification_uri").GetString().Should().EndWith("/connect/verify");
        return new Device(json.GetProperty("device_code").GetString()!, json.GetProperty("user_code").GetString()!);
    }

    private async Task<(JsonElement tokens, string? error)> PollAsync(Device device, CancellationToken ct)
    {
        using var response = await _factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["grant_type"] = DeviceGrant, ["device_code"] = device.DeviceCode, ["client_id"] = Client }), ct);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return response.IsSuccessStatusCode ? (json, null) : (json, json.GetProperty("error").GetString());
    }

    private static async Task<HttpResponseMessage> DecideAsync(HttpClient browser, string page, string userCode, string decision, CancellationToken ct)
    {
        var token = Regex.Match(page, """name="__RequestVerificationToken" value="([^"]+)""").Groups[1].Value;
        token.Should().NotBeEmpty();
        return await browser.PostAsync("/connect/verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["user_code"] = userCode,
            ["decision"] = decision,
            ["__RequestVerificationToken"] = token,
        }), ct);
    }

    private HttpClient Browser() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task SignInAsync(HttpClient browser, CancellationToken ct)
    {
        var page = await browser.GetStringAsync("/account/login", ct);
        var token = Regex.Match(page, """name="__RequestVerificationToken" value="([^"]+)""").Groups[1].Value;
        using var response = await browser.PostAsync("/account/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["email"] = Email, ["password"] = Password, ["__RequestVerificationToken"] = token }), ct);
        response.StatusCode.Should().Be(HttpStatusCode.Found);
    }

    private static string? Claim(string jwt, string name)
    {
        var claims = JsonDocument.Parse(WebEncoders.Base64UrlDecode(jwt.Split('.')[1])).RootElement;
        return claims.TryGetProperty(name, out var value) ? value.ToString() : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
        if (Directory.Exists(_keyRing)) Directory.Delete(_keyRing, recursive: true);
    }
}
