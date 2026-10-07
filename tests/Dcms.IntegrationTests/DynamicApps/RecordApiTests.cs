using System.Net;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;

namespace Dcms.IntegrationTests.DynamicApps;

/// <summary>
/// Records of published tables through the admin routes (ADR 0021): CRUD, the query engine
/// against real SQL, lookups and their delete behaviours, many-to-many links, uniqueness,
/// optimistic concurrency, and what a publish checks against the data that already exists.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class RecordApiTests(ContentFlowFixture fixture)
{
    private const string Crm = """
        [
          { "op": "create", "type": "choiceSet", "value": { "apiName": "stage", "displayName": "Stage",
              "options": [ { "value": "lead", "label": "Lead" }, { "value": "won", "label": "Won" } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "companies", "displayName": "Company", "primaryFieldId": "name",
              "fields": [ { "apiName": "name", "displayName": "Name", "required": true, "searchable": true, "sortable": true } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "deals", "displayName": "Deal", "primaryFieldId": "title",
              "fields": [
                { "apiName": "title", "displayName": "Title", "required": true, "sortable": true },
                { "apiName": "amount", "displayName": "Amount", "type": "decimal", "sortable": true },
                { "apiName": "stage", "displayName": "Stage", "type": "choice", "choiceSetId": "stage", "default": "lead" },
                { "apiName": "contact", "displayName": "Contact", "type": "email", "unique": true } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "products", "displayName": "Product",
              "fields": [ { "apiName": "sku", "displayName": "SKU", "required": true } ] } },
          { "op": "create", "type": "relationship", "value": { "apiName": "company", "sourceTableId": "deals",
              "targetTableId": "companies", "inverseApiName": "deals", "onDelete": "restrict" } },
          { "op": "create", "type": "relationship", "value": { "apiName": "products", "kind": "manyToMany", "sourceTableId": "deals",
              "targetTableId": "products", "inverseApiName": "deals" } }
        ]
        """;

    [DockerFact]
    public async Task Records_are_created_queried_related_and_deleted_through_the_published_model()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);

        // Before anything is published there are no tables to write to.
        (await app.SendAsync(HttpMethod.Post, "/_records/companies", new { name = "Acme" }, ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await app.PublishAsync(Crm, ct);

        var acme = await Create(app, "companies", new { name = "Acme" }, ct);
        var globex = await Create(app, "companies", new { name = "Globex" }, ct);
        var deals = new List<string>();
        foreach (var (title, amount, company) in new[] { ("Alpha", 500m, acme), ("Bravo", 25_000m, acme), ("Charlie", 12_000m, globex), ("Delta", 9_000m, globex) })
        {
            deals.Add(await Create(app, "deals", new { title, amount, company }, ct));
        }

        var one = await app.JsonAsync(HttpMethod.Get, $"/_records/deals/{deals[1]}?expand=company", ct);
        one.GetProperty("stage").GetString().Should().Be("lead", "the default");
        one.GetProperty("company").GetProperty("name").GetString().Should().Be("Acme");
        one.GetProperty("version").GetInt32().Should().Be(1);

        // Filter + sort + paging + a filter through the lookup, all in SQL.
        var big = await app.JsonAsync(HttpMethod.Post, "/_records/deals/query", ct, new
        {
            filter = new { and = new object[] { new { field = "amount", op = "gte", value = 9000 }, new { field = "company.name", op = "eq", value = "Globex" } } },
            sort = new[] { new { field = "amount", direction = "desc" } },
        });
        big.GetProperty("total").GetInt32().Should().Be(2);
        big.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()).Should().Equal("Charlie", "Delta");

        var paged = await app.JsonAsync(HttpMethod.Get, "/_records/deals?sort=-amount&pageSize=3&page=2&select=title", ct);
        paged.GetProperty("total").GetInt32().Should().Be(4);
        var last = paged.GetProperty("items").EnumerateArray().Single();
        last.GetProperty("title").GetString().Should().Be("Alpha");
        last.TryGetProperty("amount", out _).Should().BeFalse("only selected fields come back");

        (await app.JsonAsync(HttpMethod.Get, "/_records/companies?search=glob", ct)).GetProperty("total").GetInt32().Should().Be(1);
        (await app.JsonAsync(HttpMethod.Get, $"/_records/companies/{acme}/deals", ct)).GetProperty("total").GetInt32().Should().Be(2);

        // Many-to-many, from both sides.
        var widget = await Create(app, "products", new { sku = "W-1" }, ct);
        await app.JsonAsync(HttpMethod.Post, $"/_records/deals/{deals[0]}/products", ct, new { targetId = widget }, HttpStatusCode.NoContent);
        await app.JsonAsync(HttpMethod.Post, $"/_records/deals/{deals[0]}/products", ct, new { targetId = widget }, HttpStatusCode.NoContent);
        (await app.JsonAsync(HttpMethod.Get, $"/_records/products/{widget}/deals", ct)).GetProperty("total").GetInt32().Should().Be(1);
        await app.JsonAsync(HttpMethod.Delete, $"/_records/deals/{deals[0]}/products/{widget}", ct, expect: HttpStatusCode.NoContent);
        (await app.JsonAsync(HttpMethod.Get, $"/_records/deals/{deals[0]}/products", ct)).GetProperty("total").GetInt32().Should().Be(0);

        // Restrict: a company with deals cannot go; once its deals are gone it can.
        (await app.SendAsync(HttpMethod.Delete, $"/_records/companies/{acme}", null, ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await app.JsonAsync(HttpMethod.Post, "/_records/deals/bulk-delete", ct, new { ids = new[] { deals[0], deals[1] } });
        await app.JsonAsync(HttpMethod.Delete, $"/_records/companies/{acme}", ct, expect: HttpStatusCode.NoContent);
        (await app.SendAsync(HttpMethod.Get, $"/_records/companies/{acme}", null, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task Writes_are_validated_unique_and_versioned()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Crm, ct);

        var invalid = await app.JsonAsync(HttpMethod.Post, "/_records/deals", ct,
            new { amount = "lots", stage = "lost", company = Guid.NewGuid(), nope = 1 }, HttpStatusCode.BadRequest);
        invalid.GetProperty("fields").EnumerateObject().Select(f => f.Name)
            .Should().BeEquivalentTo(["title", "amount", "stage", "nope"], "the missing lookup target is checked once the values are well-formed");
        var dangling = await app.JsonAsync(HttpMethod.Post, "/_records/deals", ct, new { title = "X", company = Guid.NewGuid() }, HttpStatusCode.BadRequest);
        dangling.GetProperty("fields").GetProperty("company").GetString().Should().Contain("no companies record");

        var first = await Create(app, "deals", new { title = "A", contact = "ada@example.com" }, ct);
        (await app.SendAsync(HttpMethod.Post, "/_records/deals", new { title = "B", contact = "ADA@example.com" }, ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "a unique email ignores case");
        var second = await Create(app, "deals", new { title = "B" }, ct);
        (await app.SendAsync(HttpMethod.Patch, $"/_records/deals/{second}", new { contact = "ada@EXAMPLE.com" }, ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Clearing the value releases it.
        await app.JsonAsync(HttpMethod.Patch, $"/_records/deals/{first}", ct, new { contact = (string?)null });
        var moved = await app.JsonAsync(HttpMethod.Patch, $"/_records/deals/{second}", ct, new { contact = "ada@example.com", version = 1 });
        moved.GetProperty("version").GetInt32().Should().Be(2);

        // A stale version is a conflict, not a lost update.
        (await app.SendAsync(HttpMethod.Patch, $"/_records/deals/{second}", new { title = "B2", version = 1 }, ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await app.SendAsync(HttpMethod.Delete, $"/_records/deals/{second}?version=1", null, ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Concurrent claims of one unique value: exactly one wins.
        var racers = Enumerable.Range(0, 5).Select(i =>
            app.SendAsync(HttpMethod.Post, "/_records/deals", new { title = $"R{i}", contact = "race@example.com" }, ct));
        (await Task.WhenAll(racers)).Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
    }

    [DockerFact]
    public async Task A_publish_is_checked_against_the_records_that_exist()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Crm, ct);
        await Create(app, "companies", new { name = "Acme" }, ct);
        await Create(app, "companies", new { name = "Acme" }, ct);

        // A new required field without a default: the existing records would have no value.
        var refused = await Draft(app, """[ { "op": "create", "type": "field", "target": "companies", "value": { "apiName": "country", "displayName": "Country", "required": true } } ]""", ct);
        // Validate and preview already say so: what publish would refuse is no surprise.
        (await app.JsonAsync(HttpMethod.Post, "/_model/draft/validate", ct)).GetProperty("issues").EnumerateArray()
            .Select(i => i.GetProperty("code").GetString()).Should().Contain("required-without-data");
        var preview = await app.JsonAsync(HttpMethod.Get, "/_model/draft/preview", ct);
        preview.GetProperty("canPublish").GetBoolean().Should().BeFalse();
        preview.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("code").GetString()).Should().Contain("required-without-data");
        // What the records hold is only told to someone who may read them.
        var designer = await app.AddMemberAsync(["plugin:dynamic-apps:model-read"], ct);
        (await app.JsonAsync(HttpMethod.Get, "/_model/draft/preview", ct, @as: designer)).GetProperty("issues").EnumerateArray()
            .Select(i => i.GetProperty("code").GetString()).Should().NotContain("required-without-data");
        var result = await app.JsonAsync(HttpMethod.Post, "/_model/draft/publish", ct, new { expectedHash = refused }, HttpStatusCode.UnprocessableEntity);
        result.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("code").GetString()).Should().Contain("required-without-data");
        await app.JsonAsync(HttpMethod.Post, "/_model/draft/discard", ct, new { expectedHash = refused }, HttpStatusCode.NoContent);

        // Making a field unique when the records already disagree.
        var unique = await Draft(app, """[ { "op": "update", "type": "field", "target": "companies.name", "value": { "unique": true } } ]""", ct);
        (await app.JsonAsync(HttpMethod.Get, "/_model/draft/preview", ct)).GetProperty("issues").EnumerateArray()
            .Select(i => i.GetProperty("code").GetString()).Should().Contain("unique-violated-by-data");
        result = await app.JsonAsync(HttpMethod.Post, "/_model/draft/publish", ct, new { expectedHash = unique }, HttpStatusCode.UnprocessableEntity);
        result.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("code").GetString()).Should().Contain("unique-violated-by-data");

        // Fix the data, publish, and the rule now holds for existing records too.
        var companies = (await app.JsonAsync(HttpMethod.Get, "/_records/companies", ct)).GetProperty("items").EnumerateArray().ToList();
        await app.JsonAsync(HttpMethod.Patch, $"/_records/companies/{companies[0].GetProperty("id").GetString()}", ct, new { name = "Acme Ltd" });
        await app.JsonAsync(HttpMethod.Post, "/_model/draft/publish", ct, new { expectedHash = unique });
        (await app.SendAsync(HttpMethod.Post, "/_records/companies", new { name = "Acme Ltd" }, ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [DockerFact]
    public async Task Data_permissions_are_separate_from_configuration_permissions()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Crm, ct);
        var id = await Create(app, "companies", new { name = "Acme" }, ct);

        var reader = await app.AddMemberAsync(["plugin:dynamic-apps:data-read"], ct);
        (await app.SendAsync(HttpMethod.Get, $"/_records/companies/{id}", null, ct, reader)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await app.SendAsync(HttpMethod.Post, "/_records/companies", new { name = "X" }, ct, reader)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await app.SendAsync(HttpMethod.Get, "/_model", null, ct, reader)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var writer = await app.AddMemberAsync(["plugin:dynamic-apps:data-read", "plugin:dynamic-apps:data-write"], ct);
        (await app.SendAsync(HttpMethod.Patch, $"/_records/companies/{id}", new { name = "Acme 2" }, ct, writer)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await app.SendAsync(HttpMethod.Delete, $"/_records/companies/{id}", null, ct, writer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Another tenant's app with the same table names sees none of these records.
        var other = await AppHarness.CreateAsync(fixture, ct);
        await other.PublishAsync(Crm, ct);
        (await other.JsonAsync(HttpMethod.Get, "/_records/companies", ct)).GetProperty("total").GetInt32().Should().Be(0);
        (await other.SendAsync(HttpMethod.Get, $"/_records/companies/{id}", null, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<string> Create(AppHarness app, string table, object values, CancellationToken ct) =>
        (await app.JsonAsync(HttpMethod.Post, $"/_records/{table}", ct, values, HttpStatusCode.Created)).GetProperty("id").GetString()!;

    private static async Task<string> Draft(AppHarness app, string operations, CancellationToken ct)
    {
        var hash = (await app.JsonAsync(HttpMethod.Get, "/_model", ct)).GetProperty("hash").GetString();
        return (await app.JsonAsync(HttpMethod.Post, "/_model/draft/changes", ct, new { expectedHash = hash, operations = AppHarness.Operations(operations) }))
            .GetProperty("draft").GetProperty("hash").GetString()!;
    }
}
