using System.Net;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;

namespace Dcms.IntegrationTests.DynamicApps;

/// <summary>
/// Every change to an application's records and configuration writes its runtime event in the
/// same transaction (ADR 0021): an event exists exactly when its change does.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class OutboxEventTests(ContentFlowFixture fixture)
{
    private const string Model = """
        [
          { "op": "create", "type": "table", "value": { "apiName": "companies", "displayName": "Company",
              "fields": [ { "apiName": "name", "displayName": "Name", "required": true, "unique": true } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "deals", "displayName": "Deal",
              "fields": [ { "apiName": "title", "displayName": "Title", "required": true }, { "apiName": "status", "displayName": "Status" } ] } },
          { "op": "create", "type": "relationship", "value": { "apiName": "company", "sourceTableId": "deals",
              "targetTableId": "companies", "inverseApiName": "deals", "onDelete": "setNull" } },
          { "op": "create", "type": "relationship", "value": { "apiName": "partners", "kind": "manyToMany", "sourceTableId": "companies",
              "targetTableId": "companies", "inverseApiName": "partner_of" } }
        ]
        """;

    [DockerFact]
    public async Task Each_change_writes_its_event_and_a_refused_change_writes_none()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Model, ct);

        var acme = await Create(app, "companies", new { name = "Acme" }, ct);
        var globex = await Create(app, "companies", new { name = "Globex" }, ct);
        var deal = await Create(app, "deals", new { title = "Big", company = acme }, ct);
        await app.JsonAsync(HttpMethod.Patch, $"/_records/deals/{deal}", ct, new { status = "won", title = "Big" });
        await app.JsonAsync(HttpMethod.Post, $"/_records/companies/{acme}/partners", ct, new { targetId = globex }, HttpStatusCode.NoContent);
        await app.JsonAsync(HttpMethod.Delete, $"/_records/companies/{acme}/partners/{globex}", ct, expect: HttpStatusCode.NoContent);

        // Refused writes leave nothing behind.
        await app.JsonAsync(HttpMethod.Post, "/_records/companies", ct, new { name = "Acme" }, HttpStatusCode.Conflict);
        await app.JsonAsync(HttpMethod.Post, "/_records/deals", ct, new { status = "x" }, HttpStatusCode.BadRequest);

        // Set null: deleting the company updates the deal that pointed at it.
        await app.JsonAsync(HttpMethod.Delete, $"/_records/companies/{acme}", ct, expect: HttpStatusCode.NoContent);

        var events = await app.OutboxAsync(ct);
        events.Select(e => e.Name).Should().Equal(
            "revision.published",
            "row.created", "row.created", "row.created",
            "row.updated",
            "relation.created", "relation.deleted",
            "row.updated", "row.deleted");

        var published = events[0].Envelope;
        published.GetProperty("payload").GetProperty("number").GetInt32().Should().Be(1);
        published.GetProperty("entity").GetProperty("type").GetString().Should().Be("app");

        var updated = events[4].Envelope;
        updated.GetProperty("entity").GetProperty("type").GetString().Should().Be("deals");
        updated.GetProperty("changedFields").EnumerateArray().Select(f => f.GetString()).Should().Equal("status");
        updated.GetProperty("payload").GetProperty("record").GetProperty("status").GetString().Should().Be("won");
        updated.GetProperty("payload").GetProperty("previous").GetProperty("status").ValueKind.Should().Be(JsonValueKind.Null);
        updated.GetProperty("depth").GetInt32().Should().Be(0);

        var link = events[5].Envelope.GetProperty("payload");
        (link.GetProperty("relationship").GetString(), link.GetProperty("sourceId").GetString(), link.GetProperty("targetId").GetString())
            .Should().Be(("partners", acme, globex));

        var cleared = events[7].Envelope;
        cleared.GetProperty("entity").GetProperty("id").GetString().Should().Be(deal);
        cleared.GetProperty("changedFields").EnumerateArray().Single().GetString().Should().Be("company");
        cleared.GetProperty("payload").GetProperty("previous").GetProperty("company").GetString().Should().Be(acme);
        events[8].Envelope.GetProperty("payload").GetProperty("record").GetProperty("name").GetString().Should().Be("Acme");

        // One request, one correlation: the set-null update and the delete belong together.
        cleared.GetProperty("correlationId").GetGuid().Should().Be(events[8].Envelope.GetProperty("correlationId").GetGuid());
        cleared.GetProperty("correlationId").GetGuid().Should().NotBe(updated.GetProperty("correlationId").GetGuid());
    }

    [DockerFact]
    public async Task A_refused_publish_announces_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        var hash = (await app.JsonAsync(HttpMethod.Get, "/_model", ct)).GetProperty("hash").GetString();
        var draft = (await app.JsonAsync(HttpMethod.Post, "/_model/draft/changes", ct, new
        {
            expectedHash = hash,
            operations = AppHarness.Operations("""[ { "op": "create", "type": "table", "value": { "apiName": "Bad", "displayName": "B" } } ]"""),
        })).GetProperty("draft").GetProperty("hash").GetString();
        await app.JsonAsync(HttpMethod.Post, "/_model/draft/publish", ct, new { expectedHash = draft }, HttpStatusCode.UnprocessableEntity);

        (await app.OutboxAsync(ct)).Should().BeEmpty();
    }

    private static async Task<string> Create(AppHarness app, string table, object values, CancellationToken ct) =>
        (await app.JsonAsync(HttpMethod.Post, $"/_records/{table}", ct, values, HttpStatusCode.Created)).GetProperty("id").GetString()!;
}
