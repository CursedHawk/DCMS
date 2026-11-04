using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;

namespace Dcms.IntegrationTests.DynamicApps;

/// <summary>
/// The assistant's surface (ADR 0021): dynamic-apps.config@1 and dynamic-apps.records@1 on the
/// AI plane — only for an instance opted in to AI tools, filtered and enforced by the member's
/// own permissions, writing to a draft, and leaving a trace from conversation to revision.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class AiContractTests(ContentFlowFixture fixture)
{
    private const string Crm = """
        [ { "op": "create", "type": "table", "value": { "apiName": "companies", "displayName": "Company", "primaryFieldId": "name",
              "fields": [ { "apiName": "name", "displayName": "Name", "required": true } ] } } ]
        """;

    [DockerFact]
    public async Task An_agent_changes_a_draft_with_the_members_permissions_and_leaves_a_trace()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        var conversation = Guid.NewGuid();
        var run = Guid.NewGuid();

        // Not opted in: the tools do not exist on the AI plane at all.
        (await Call(app, "config", "GetSummary", new { }, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var enabled = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Put, $"/api/admin/plugins/instances/{app.InstanceId}", app.Owner, app.Tenant,
            body: new { aiToolsEnabled = true }), ct);
        enabled.IsSuccessStatusCode.Should().BeTrue(await enabled.Content.ReadAsStringAsync(ct));

        var catalog = await CatalogAsync(app, app.Owner, ct);
        catalog.Should().Contain(["dynamic-apps.config/GetSummary", "dynamic-apps.config/ApplyChangeSet", "dynamic-apps.config/PublishDraft",
            "dynamic-apps.records/QueryRecords"]);

        var summary = await Json(await Call(app, "config", "GetSummary", new { }, ct), ct);
        var hash = summary.GetProperty("hash").GetString();

        var stale = await Call(app, "config", "ApplyChangeSet", new { expectedHash = "nope", operations = AppHarness.Operations(Crm) }, ct);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict, "a stale hash is refused, never an overwrite");

        var applied = await Json(await Call(app, "config", "ApplyChangeSet", new { expectedHash = hash, operations = AppHarness.Operations(Crm), description = "CRM" },
            ct, conversation, run, "toolu_1"), ct);
        var draft = applied.GetProperty("draft");
        draft.GetProperty("status").GetString().Should().Be("draft", "an agent's change always lands in a draft");
        draft.GetProperty("source").GetString().Should().Be("ai");
        draft.GetProperty("sourceConversationId").GetGuid().Should().Be(conversation);
        draft.GetProperty("sourceAiRunId").GetGuid().Should().Be(run);
        (await app.SendAsync(HttpMethod.Get, "/_model/published", null, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound, "nothing went live");

        // The change log names the tool call that made each change.
        await using (var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """SELECT DISTINCT "ToolCallId", "AiConversationId" FROM apps.changes WHERE "RevisionId" = @r""";
            cmd.Parameters.AddWithValue("r", draft.GetProperty("id").GetGuid());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            (await reader.ReadAsync(ct)).Should().BeTrue();
            (reader.GetString(0), reader.GetGuid(1)).Should().Be(("toolu_1", conversation));
        }

        var preview = await Json(await Call(app, "config", "PreviewDraft", null, ct), ct);
        preview.GetProperty("canPublish").GetBoolean().Should().BeTrue();

        // A member who may edit but not publish gets the edit tools and not publishing — in the
        // catalog, and when the call is made anyway.
        var editor = await app.AddMemberAsync(["plugin:dynamic-apps:model-read", "plugin:dynamic-apps:model-write"], ct);
        var editorTools = await CatalogAsync(app, editor, ct);
        editorTools.Should().Contain("dynamic-apps.config/ApplyChangeSet").And.NotContain("dynamic-apps.config/PublishDraft")
            .And.NotContain("dynamic-apps.records/QueryRecords");
        (await Call(app, "config", "PublishDraft", new { expectedHash = draft.GetProperty("hash").GetString() }, ct, @as: editor))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var published = await Json(await Call(app, "config", "PublishDraft", new { expectedHash = draft.GetProperty("hash").GetString() }, ct, conversation, run, "toolu_2"), ct);
        published.GetProperty("published").GetBoolean().Should().BeTrue();

        // Records through the same contract and the same rules as the admin.
        var created = await Json(await Call(app, "records", "CreateRecord", new { table = "companies", values = new { name = "Acme" } }, ct), ct);
        (await Call(app, "records", "CreateRecord", new { table = "companies", values = new { nope = 1 } }, ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var page = await Json(await Call(app, "records", "QueryRecords", new { table = "companies", query = new { search = "acm" } }, ct), ct);
        page.GetProperty("items")[0].GetProperty("id").GetString().Should().Be(created.GetProperty("id").GetString());
    }

    // ---- helpers ----

    private static Task<HttpResponseMessage> Call(AppHarness app, string contract, string op, object? input, CancellationToken ct,
        Guid? conversation = null, Guid? run = null, string? toolCall = null, Guid? @as = null)
    {
        var req = AppHarness.Req(HttpMethod.Post, $"/api/admin/contracts/dynamic-apps.{contract}@1/{op}?instance=crm&plane=ai", @as ?? app.Owner, app.Tenant,
            body: input ?? new { });
        if (conversation is not null) req.Headers.Add("X-Dcms-Ai-Conversation", conversation.ToString());
        if (run is not null) req.Headers.Add("X-Dcms-Ai-Run", run.ToString());
        if (toolCall is not null) req.Headers.Add("X-Dcms-Ai-Tool-Call", toolCall);
        return app.Admin.SendAsync(req, ct);
    }

    private static async Task<List<string>> CatalogAsync(AppHarness app, Guid member, CancellationToken ct)
    {
        var res = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/contracts?plane=ai", member, app.Tenant), ct);
        var catalog = await Json(res, ct);
        return catalog.EnumerateArray()
            .Where(c => c.GetProperty("id").GetString()!.StartsWith("dynamic-apps.", StringComparison.Ordinal))
            .SelectMany(c => c.GetProperty("operations").EnumerateArray()
                .Select(o => $"{c.GetProperty("id").GetString()!.Split('@')[0]}/{o.GetProperty("name").GetString()}"))
            .ToList();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res, CancellationToken ct)
    {
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(ct));
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
}
