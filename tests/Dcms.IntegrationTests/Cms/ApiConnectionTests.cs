using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dcms.IntegrationTests.Cms;

/// <summary>
/// External API connections (Mode D backlog #124), across both planes: admin-api calls the
/// tenant's API with the credential and stores each allowed operation's response; content-api
/// serves exactly those snapshots, per tenant, and nothing else.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public class ApiConnectionTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    [DockerFact]
    public async Task A_connection_syncs_its_operations_with_the_key_and_sites_read_only_those_snapshots()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = await ProviderStub.StartAsync();
        var admin = fixture.Admin.CreateClient();
        var content = fixture.Content.CreateClient();
        var owner = Guid.NewGuid();
        var slug = "conn-" + Guid.NewGuid().ToString("N")[..8];
        var other = "conn-other-" + Guid.NewGuid().ToString("N")[..8];
        await CreateTenant(admin, slug, owner, ct);
        await CreateTenant(admin, other, Guid.NewGuid(), ct);

        var saved = await admin.SendAsync(AdminReq(HttpMethod.Put, "/api/admin/connections/tickets", owner, slug, new
        {
            name = "Ticket shop",
            baseUrl = provider.BaseUrl + "/v1",
            authKind = "header",
            authName = "X-Api-Key",
            secret = "s3cret-key",
            operations = new[] { "/events", "/events?city=brno" },
            refreshMinutes = 30,
        }), ct);
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await saved.Content.ReadAsStringAsync(ct);
        // Write-only: neither the key nor its ciphertext ever comes back.
        view.Should().NotContain("s3cret-key").And.NotContain("vault:");
        JsonDocument.Parse(view).RootElement.GetProperty("hasSecret").GetBoolean().Should().BeTrue();
        JsonDocument.Parse(view).RootElement.GetProperty("lastError").ValueKind.Should().Be(JsonValueKind.Null);

        // The provider saw the key, in the header the tenant named, for each operation only.
        provider.Requests.Should().BeEquivalentTo(["/v1/events key=s3cret-key", "/v1/events?city=brno key=s3cret-key"]);

        // Stored under the connection's own Transit key, never as plaintext.
        await using (var db = new NpgsqlConnection(fixture.PostgresConnectionString))
        {
            await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("""SELECT "SecretCiphertext" FROM cms.api_connections WHERE "Slug" = 'tickets'""", db);
            var stored = (string?)await cmd.ExecuteScalarAsync(ct);
            stored.Should().StartWith("vault:fake:dcms-api-connections:").And.NotContain("s3cret-key");
        }

        // Sites read the snapshot, query and all, from content-api — which never calls out.
        var before = provider.Requests.Count;
        var list = await content.SendAsync(TenantReq(slug, "/api/connections/tickets/events"), ct);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await list.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items")[0].GetProperty("name").GetString().Should().Be("Gala");
        var brno = await content.SendAsync(TenantReq(slug, "/api/connections/tickets/events?city=brno"), ct);
        (await brno.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items")[0].GetProperty("name").GetString().Should().Be("Brno night");
        provider.Requests.Count.Should().Be(before);

        // Only the listed operations exist, and only for this tenant.
        (await content.SendAsync(TenantReq(slug, "/api/connections/tickets/events?city=praha"), ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await content.SendAsync(TenantReq(slug, "/api/connections/tickets/admin/users"), ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await content.SendAsync(TenantReq(other, "/api/connections/tickets/events"), ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A failing provider keeps the last good snapshot and says what went wrong.
        provider.Failing = true;
        var refreshed = await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/connections/tickets/refresh", owner, slug), ct);
        (await refreshed.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("lastError").GetString().Should().Contain("503");
        (await content.SendAsync(TenantReq(slug, "/api/connections/tickets/events"), ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        // The list never carries the key either.
        var listing = await (await admin.SendAsync(AdminReq(HttpMethod.Get, "/api/admin/connections", owner, slug), ct)).Content.ReadAsStringAsync(ct);
        listing.Should().Contain("\"tickets\"").And.NotContain("s3cret-key").And.NotContain("vault:");
        // And it says what each operation's items look like, for the builder's field picker.
        var shape = JsonDocument.Parse(listing).RootElement[0].GetProperty("shapes")[0];
        shape.GetProperty("items").GetString().Should().Be("items");
        shape.GetProperty("fields").EnumerateArray().Select(f => f.GetString()).Should().BeEquivalentTo(["id", "name"]);
    }

    [DockerFact]
    public async Task Addresses_that_could_aim_the_key_elsewhere_are_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = fixture.Admin.CreateClient();
        var owner = Guid.NewGuid();
        var slug = "conn-bad-" + Guid.NewGuid().ToString("N")[..8];
        await CreateTenant(admin, slug, owner, ct);

        async Task<string> Refused(object body)
        {
            var res = await admin.SendAsync(AdminReq(HttpMethod.Put, "/api/admin/connections/bad", owner, slug, body), ct);
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("error").GetString()!;
        }
        object Body(string baseUrl, params string[] operations) =>
            new { name = "x", baseUrl, authKind = "bearer", secret = "k", operations, refreshMinutes = 60 };

        (await Refused(Body("https://user:pw@api.example.com", "/a"))).Should().Contain("credentials");
        (await Refused(Body("https://api.example.com?key=1", "/a"))).Should().Contain("query");
        (await Refused(Body("https://api.example.com", "//evil.example.com/steal"))).Should().Contain("operation");
        (await Refused(Body("https://api.example.com", "/a/../admin"))).Should().Contain("..");
        (await Refused(Body("https://api.example.com", "/a#frag"))).Should().Contain("operation");
        (await Refused(new { name = "x", baseUrl = "https://api.example.com", authKind = "header", authName = "Host", secret = "k", operations = new[] { "/a" } }))
            .Should().Contain("Host");
        (await Refused(new { name = "x", baseUrl = "https://api.example.com", authKind = "bearer", operations = new[] { "/a" } }))
            .Should().Contain("needs a key");
    }

    private static HttpRequestMessage TenantReq(string slug, string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Dcms-Tenant", slug);
        return req;
    }

    private static HttpRequestMessage AdminReq(HttpMethod method, string url, Guid sub, string slug, object? body = null, string roles = "")
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (!string.IsNullOrEmpty(roles)) req.Headers.Add("X-Test-Roles", roles);
        if (slug.Length > 0) req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task CreateTenant(HttpClient admin, string slug, Guid owner, CancellationToken ct)
    {
        var res = await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "",
            new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }, "SuperAdmin"), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    /// <summary>A tenant's API: answers with a key, records what it was asked and with which key.</summary>
    private sealed class ProviderStub : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private ProviderStub(WebApplication app) => _app = app;
        public string BaseUrl { get; private set; } = string.Empty;
        public ConcurrentQueue<string> Requests { get; } = new();
        public bool Failing { get; set; }

        public static async Task<ProviderStub> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Urls.Add("http://127.0.0.1:0");
            var stub = new ProviderStub(app);
            app.MapGet("/v1/events", (HttpContext http) =>
            {
                stub.Requests.Enqueue($"{http.Request.Path}{http.Request.QueryString} key={http.Request.Headers["X-Api-Key"]}");
                if (stub.Failing) return Results.StatusCode(503);
                var city = http.Request.Query["city"].ToString();
                return Results.Json(new { items = new[] { new { id = 1, name = city == "brno" ? "Brno night" : "Gala" } } });
            });
            await app.StartAsync();
            stub.BaseUrl = app.Urls.First();
            return stub;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
