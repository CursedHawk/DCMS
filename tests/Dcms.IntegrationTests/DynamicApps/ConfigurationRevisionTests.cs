using System.Net;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.DynamicApps;

/// <summary>
/// Configuration revisions through the admin routes (ADR 0021): draft → publish → change →
/// publish → rollback, the optimistic-concurrency guarantees, a refused publish, permissions,
/// audit and tenant isolation — against the real admin host and database.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class ConfigurationRevisionTests(ContentFlowFixture fixture)
{
    private const string Crm = """
        [
          { "op": "create", "type": "table", "value": { "apiName": "companies", "displayName": "Company", "primaryFieldId": "name",
              "fields": [ { "apiName": "name", "displayName": "Name", "type": "text", "required": true } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "contacts", "displayName": "Contact", "primaryFieldId": "email",
              "fields": [ { "apiName": "email", "displayName": "Email", "type": "email", "unique": true } ] } },
          { "op": "create", "type": "relationship", "value": { "apiName": "company", "sourceTableId": "contacts",
              "targetTableId": "companies", "inverseApiName": "contacts" } }
        ]
        """;

    private static object Changes(string expectedHash, string operations) =>
        new { expectedHash, operations = AppHarness.Operations(operations) };

    [DockerFact]
    public async Task A_draft_is_published_changed_published_again_and_rolled_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);

        var state = await app.JsonAsync(HttpMethod.Get, "/_model", ct);
        state.GetProperty("draft").ValueKind.Should().Be(JsonValueKind.Null);
        var emptyHash = state.GetProperty("hash").GetString()!;

        // A stale hash is a conflict, never an overwrite.
        (await app.SendAsync(HttpMethod.Post, "/_model/draft/changes", Changes("0000", Crm), ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // The first change opens the draft.
        var applied = await app.JsonAsync(HttpMethod.Post, "/_model/draft/changes", ct, Changes(emptyHash, Crm));
        var r1 = applied.GetProperty("draft");
        r1.GetProperty("number").GetInt32().Should().Be(1);
        r1.GetProperty("status").GetString().Should().Be("draft");
        applied.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("path").GetString())
            .Should().Contain(["companies", "contacts.email", "contacts.company"]);
        var r1Hash = r1.GetProperty("hash").GetString()!;

        var preview = await app.JsonAsync(HttpMethod.Get, "/_model/draft/preview", ct);
        preview.GetProperty("canPublish").GetBoolean().Should().BeTrue(preview.ToString());

        (await app.SendAsync(HttpMethod.Post, "/_model/draft/publish", new { expectedHash = emptyHash }, ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        var published = await app.JsonAsync(HttpMethod.Post, "/_model/draft/publish", ct, new { expectedHash = r1Hash });
        published.GetProperty("revision").GetProperty("status").GetString().Should().Be("published");

        // The next change opens r2 from r1.
        var r2 = (await app.JsonAsync(HttpMethod.Post, "/_model/draft/changes", ct, Changes(r1Hash, """
            [ { "op": "create", "type": "field", "target": "companies", "value": { "apiName": "revenue", "displayName": "Revenue", "type": "decimal" } } ]
            """))).GetProperty("draft");
        r2.GetProperty("number").GetInt32().Should().Be(2);
        r2.GetProperty("basePublishedId").GetGuid().Should().Be(r1.GetProperty("id").GetGuid());
        (await app.JsonAsync(HttpMethod.Get, "/_model/published", ct))
            .GetProperty("revision").GetProperty("number").GetInt32().Should().Be(1, "a draft never changes what is live");

        await app.JsonAsync(HttpMethod.Post, "/_model/draft/publish", ct, new { expectedHash = r2.GetProperty("hash").GetString() });

        // Rollback is a new revision with r1's content; r2 stays, marked rolled back.
        var r3 = (await app.JsonAsync(HttpMethod.Post, "/_model/rollback", ct, new { toRevision = 1 })).GetProperty("revision");
        r3.GetProperty("number").GetInt32().Should().Be(3);
        r3.GetProperty("parentId").GetGuid().Should().Be(r1.GetProperty("id").GetGuid());
        (await app.JsonAsync(HttpMethod.Get, "/_model/diff?from=1&to=3", ct)).GetProperty("changes").GetArrayLength()
            .Should().Be(0, "a rollback restores the earlier configuration exactly");

        (await app.JsonAsync(HttpMethod.Get, "/_model/revisions", ct)).GetProperty("items").EnumerateArray()
            .Select(r => r.GetProperty("status").GetString())
            .Should().Equal("published", "rolledBack", "superseded");

        var diff = await app.JsonAsync(HttpMethod.Get, "/_model/diff?from=2&to=3", ct);
        diff.GetProperty("changes").EnumerateArray().Single().GetProperty("path").GetString().Should().Be("companies.revenue");
        (await app.JsonAsync(HttpMethod.Get, "/_model/revisions/3/changes", ct))
            .EnumerateArray().Single().GetProperty("op").GetString().Should().Be("delete");

        await AuditedAsync("plugin.dynamic-apps.revision.rolled_back", ct);
    }

    [DockerFact]
    public async Task An_invalid_draft_is_refused_publication_and_stays_editable()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        var hash = (await app.JsonAsync(HttpMethod.Get, "/_model", ct)).GetProperty("hash").GetString()!;

        var applied = await app.JsonAsync(HttpMethod.Post, "/_model/draft/changes", ct, Changes(hash, """
            [ { "op": "create", "type": "table", "value": { "apiName": "Bad Name", "displayName": "Bad" } } ]
            """));
        applied.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("code").GetString()).Should().Contain("invalid-api-name");
        var draftHash = applied.GetProperty("draft").GetProperty("hash").GetString();

        var refused = await app.JsonAsync(HttpMethod.Post, "/_model/draft/publish", ct, new { expectedHash = draftHash },
            HttpStatusCode.UnprocessableEntity);
        refused.GetProperty("published").GetBoolean().Should().BeFalse();

        var state = await app.JsonAsync(HttpMethod.Get, "/_model", ct);
        state.GetProperty("published").ValueKind.Should().Be(JsonValueKind.Null);
        state.GetProperty("draft").GetProperty("hash").GetString().Should().Be(draftHash);
    }

    [DockerFact]
    public async Task Two_writers_on_the_same_hash_cannot_both_win()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        var hash = (await app.JsonAsync(HttpMethod.Get, "/_model", ct)).GetProperty("hash").GetString()!;

        var writers = Enumerable.Range(0, 4).Select(i => app.SendAsync(HttpMethod.Post, "/_model/draft/changes", Changes(hash, $$"""
            [ { "op": "create", "type": "table", "value": { "apiName": "t{{i}}", "displayName": "T{{i}}" } } ]
            """), ct));
        var codes = (await Task.WhenAll(writers)).Select(r => r.StatusCode).ToList();

        codes.Count(c => c == HttpStatusCode.OK).Should().Be(1);
        codes.Count(c => c == HttpStatusCode.Conflict).Should().Be(3);
        (await app.JsonAsync(HttpMethod.Get, "/_model/draft", ct)).GetProperty("config").GetProperty("tables").GetArrayLength().Should().Be(1);
    }

    [DockerFact]
    public async Task Permissions_gate_reading_editing_and_publishing_and_tenants_do_not_meet()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        var hash = (await app.JsonAsync(HttpMethod.Get, "/_model", ct)).GetProperty("hash").GetString()!;

        var reader = await app.AddMemberAsync(["plugin:dynamic-apps:model-read"], ct);
        (await app.SendAsync(HttpMethod.Get, "/_model", null, ct, reader)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await app.SendAsync(HttpMethod.Post, "/_model/draft/changes", Changes(hash, Crm), ct, reader)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var editor = await app.AddMemberAsync(["plugin:dynamic-apps:model-read", "plugin:dynamic-apps:model-write"], ct);
        var draftHash = (await app.JsonAsync(HttpMethod.Post, "/_model/draft/changes", ct, Changes(hash, Crm), @as: editor))
            .GetProperty("draft").GetProperty("hash").GetString();
        (await app.SendAsync(HttpMethod.Post, "/_model/draft/publish", new { expectedHash = draftHash }, ct, editor))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Another tenant's app of the same slug knows nothing of this one.
        var other = await AppHarness.CreateAsync(fixture, ct);
        (await other.JsonAsync(HttpMethod.Get, "/_model", ct)).GetProperty("draft").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private async Task AuditedAsync(string action, CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
        {
            using var scope = fixture.Admin.Services.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<Dcms.Shared.Data.Audit.AuditDbContext>();
            using var rls = Dcms.Shared.Data.Rls.RlsScope.Platform();
            if (await audit.Events.AsNoTracking().AnyAsync(e => e.Action == action, ct))
            {
                return;
            }
            await Task.Delay(250, ct);
        }
        throw new Xunit.Sdk.XunitException($"No audit record {action}.");
    }
}
