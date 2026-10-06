using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;

namespace Dcms.IntegrationTests.DynamicApps;

/// <summary>
/// The application's public API in content-api (ADR 0021): only published tables, only what
/// each table's public access allows, "own" records per signed-in visitor, hidden and read-only
/// fields kept so — and the tenant's OpenAPI document following every publish.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class PublicApiTests(ContentFlowFixture fixture)
{
    private const string Model = """
        [
          { "op": "create", "type": "table", "value": { "apiName": "products", "displayName": "Product", "primaryFieldId": "name",
              "public": { "read": "all" },
              "fields": [ { "apiName": "name", "displayName": "Name", "required": true, "sortable": true }, { "apiName": "cost", "displayName": "Cost", "type": "decimal", "hiddenFromPublic": true } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "leads", "displayName": "Lead", "public": { "create": true },
              "fields": [ { "apiName": "email", "displayName": "Email", "type": "email", "required": true } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "notes", "displayName": "Note",
              "public": { "read": "own", "create": true, "updateOwn": true, "deleteOwn": true },
              "fields": [ { "apiName": "body", "displayName": "Body", "required": true },
                          { "apiName": "status", "displayName": "Status", "readOnly": true },
                          { "apiName": "secret", "displayName": "Secret", "hiddenFromPublic": true } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "internal", "displayName": "Internal",
              "fields": [ { "apiName": "x", "displayName": "X" } ] } }
        ]
        """;

    [DockerFact]
    public async Task The_site_gets_exactly_what_each_table_allows()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        (await app.SiteAsync(HttpMethod.Get, "/api/crm/data/products", ct)).StatusCode.Should().Be(HttpStatusCode.NotFound, "nothing is published");
        await app.PublishAsync(Model, ct);
        await app.JsonAsync(HttpMethod.Post, "/_records/products", ct, new { name = "Widget", cost = 3 }, HttpStatusCode.Created);
        await app.JsonAsync(HttpMethod.Post, "/_records/internal", ct, new { x = "secret" }, HttpStatusCode.Created);

        var products = await Json(await app.SiteAsync(HttpMethod.Get, "/api/crm/data/products?sort=name", ct), ct);
        var widget = products.GetProperty("items").EnumerateArray().Single();
        widget.GetProperty("name").GetString().Should().Be("Widget");
        widget.TryGetProperty("cost", out _).Should().BeFalse("a hidden field never reaches the site");
        widget.TryGetProperty("created_by", out _).Should().BeFalse("nor who made it in the admin");

        (await app.SiteAsync(HttpMethod.Get, "/api/crm/data/internal", ct)).StatusCode.Should().Be(HttpStatusCode.NotFound, "a private table does not exist for the site");
        (await app.SiteAsync(HttpMethod.Post, "/api/crm/data/products", ct, new { name = "Mine" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await app.SiteAsync(HttpMethod.Post, "/api/crm/data/products/query", ct, new { filter = new { field = "cost", op = "gt", value = 1 } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "nor can it filter on a hidden field");

        (await app.SiteAsync(HttpMethod.Post, "/api/crm/data/leads", ct, new { email = "lead@example.com" })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await app.SiteAsync(HttpMethod.Get, "/api/crm/data/leads", ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "write-only: a form, not a list");
        (await app.JsonAsync(HttpMethod.Get, "/_records/leads", ct)).GetProperty("total").GetInt32().Should().Be(1);

        var model = await Json(await app.SiteAsync(HttpMethod.Get, "/api/crm/_model", ct), ct);
        model.GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("apiName").GetString()).Should().Equal("leads", "notes", "products");
        model.GetProperty("tables")[2].GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("apiName").GetString()).Should().Equal("name");
    }

    [DockerFact]
    public async Task Own_records_belong_to_the_visitor_who_made_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.InstallAsync("visitor-auth", "members", "{}", ct);
        await app.PublishAsync(Model, ct);

        (await app.SiteAsync(HttpMethod.Get, "/api/crm/data/notes", ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var ada = await app.VisitorAsync("ada@example.com", ct);
        var bob = await app.VisitorAsync("bob@example.com", ct);

        (await app.SiteAsync(HttpMethod.Post, "/api/crm/data/notes", ct, new { body = "x", secret = "s" }, ada)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await app.SiteAsync(HttpMethod.Post, "/api/crm/data/notes", ct, new { body = "x", status = "done" }, ada)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var note = await Json(await app.SiteAsync(HttpMethod.Post, "/api/crm/data/notes", ct, new { body = "Ada's note" }, ada), ct, HttpStatusCode.Created);
        var id = note.GetProperty("id").GetString();

        (await Json(await app.SiteAsync(HttpMethod.Get, "/api/crm/data/notes", ct, token: ada), ct)).GetProperty("total").GetInt32().Should().Be(1);
        (await Json(await app.SiteAsync(HttpMethod.Get, "/api/crm/data/notes", ct, token: bob), ct)).GetProperty("total").GetInt32().Should().Be(0);
        (await app.SiteAsync(HttpMethod.Get, $"/api/crm/data/notes/{id}", ct, token: bob)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await app.SiteAsync(HttpMethod.Patch, $"/api/crm/data/notes/{id}", ct, new { body = "Bob was here" }, bob)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await app.SiteAsync(HttpMethod.Delete, $"/api/crm/data/notes/{id}", ct, token: bob)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await Json(await app.SiteAsync(HttpMethod.Patch, $"/api/crm/data/notes/{id}", ct, new { body = "Edited", version = 1 }, ada), ct))
            .GetProperty("version").GetInt32().Should().Be(2);
        (await app.SiteAsync(HttpMethod.Patch, $"/api/crm/data/notes/{id}", ct, new { body = "Stale", version = 1 }, ada)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await app.SiteAsync(HttpMethod.Delete, $"/api/crm/data/notes/{id}", ct, token: ada)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [DockerFact]
    public async Task Drafts_never_reach_the_public_api_and_the_openapi_document_follows_each_publish()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Model, ct);

        var first = await Json(await app.SiteAsync(HttpMethod.Get, "/api/openapi.json", ct), ct);
        var list = first.GetProperty("paths").GetProperty("/api/crm/data/products").GetProperty("get");
        list.GetProperty("x-dcms-model-revision").GetInt32().Should().Be(1);
        list.GetProperty("x-dcms-client").EnumerateArray().Select(p => p.GetString()).Should().Contain("list");
        first.GetProperty("paths").EnumerateObject().Select(p => p.Name).Should().NotContain(p => p.Contains("/internal"))
            .And.Contain(["/api/crm/data/leads", "/api/crm/data/notes/{id}"]);
        first.GetProperty("components").GetProperty("schemas").GetProperty("crm_products").GetProperty("properties")
            .TryGetProperty("cost", out _).Should().BeFalse();

        // A draft change is invisible: not in the model, not accepted, not documented.
        var hash = (await app.JsonAsync(HttpMethod.Get, "/_model", ct)).GetProperty("hash").GetString();
        var draft = (await app.JsonAsync(HttpMethod.Post, "/_model/draft/changes", ct, new
        {
            expectedHash = hash,
            operations = AppHarness.Operations("""[ { "op": "create", "type": "field", "target": "leads", "value": { "apiName": "company", "displayName": "Company" } } ]"""),
        })).GetProperty("draft").GetProperty("hash").GetString();
        (await app.SiteAsync(HttpMethod.Post, "/api/crm/data/leads", ct, new { email = "a@b.test", company = "Acme" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(await app.SiteAsync(HttpMethod.Get, "/api/openapi.json", ct), ct)).GetProperty("components").GetProperty("schemas")
            .GetProperty("crm_leads_input").GetProperty("properties").TryGetProperty("company", out _).Should().BeFalse();

        await app.JsonAsync(HttpMethod.Post, "/_model/draft/publish", ct, new { expectedHash = draft });
        (await app.SiteAsync(HttpMethod.Post, "/api/crm/data/leads", ct, new { email = "a@b.test", company = "Acme" })).StatusCode.Should().Be(HttpStatusCode.Created);
        var second = await Json(await app.SiteAsync(HttpMethod.Get, "/api/openapi.json", ct), ct);
        second.GetProperty("components").GetProperty("schemas").GetProperty("crm_leads_input").GetProperty("properties")
            .TryGetProperty("company", out _).Should().BeTrue("publishing rebuilt the cached document");
        second.GetProperty("paths").GetProperty("/api/crm/data/products").GetProperty("get").GetProperty("x-dcms-model-revision").GetInt32().Should().Be(2);

        // The generated TypeScript client is built from the same published document.
        var client = await ClientSourceAsync(app, ct);
        client.Should().Contain("/api/crm/data/products").And.Contain("/api/crm/data/leads").And.NotContain("/api/crm/data/internal");
        client.Should().Contain("company", "the client follows the publish");
    }

    [DockerFact]
    public async Task Public_writes_are_throttled_per_client()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Model, ct);

        var last = HttpStatusCode.OK;
        for (var i = 0; i <= Dcms.Plugins.DynamicApps.Endpoints.PublicEndpointsLimits.MaxWritesPerHour; i++)
        {
            last = (await app.SiteAsync(HttpMethod.Post, "/api/crm/data/leads", ct, new { email = $"lead{i}@example.com" })).StatusCode;
        }
        last.Should().Be(HttpStatusCode.TooManyRequests);
        (await app.JsonAsync(HttpMethod.Post, "/_records/leads", ct, new { email = "admin@example.com" }, HttpStatusCode.Created))
            .GetProperty("email").GetString().Should().Be("admin@example.com", "members in the admin are not throttled");
    }

    private static async Task<string> ClientSourceAsync(AppHarness app, CancellationToken ct)
    {
        var res = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/api-client.zip", app.Owner, app.Tenant), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(ct));
        using var zip = new System.IO.Compression.ZipArchive(await res.Content.ReadAsStreamAsync(ct));
        var text = new System.Text.StringBuilder();
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".ts", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open());
            text.AppendLine(await reader.ReadToEndAsync(ct));
        }
        return text.ToString();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res, CancellationToken ct, HttpStatusCode expect = HttpStatusCode.OK)
    {
        res.StatusCode.Should().Be(expect, await res.Content.ReadAsStringAsync(ct));
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
}
