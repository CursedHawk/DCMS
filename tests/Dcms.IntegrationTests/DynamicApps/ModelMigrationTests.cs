using System.Net;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;

namespace Dcms.IntegrationTests.DynamicApps;

/// <summary>
/// A better model must not cost the data (ADR 0021): a text field becomes a choice field in
/// place, a choice field a multi-choice one, and a new lookup takes over the ids an old
/// "…_id" text field held (copyFrom). What does not fit is refused or reported with examples,
/// and validate and preview say so before publish does.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class ModelMigrationTests(ContentFlowFixture fixture)
{
    private const string Crm = """
        [
          { "op": "create", "type": "table", "value": { "apiName": "companies", "displayName": "Company", "primaryFieldId": "name",
              "fields": [ { "apiName": "name", "displayName": "Name", "required": true } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "deals", "displayName": "Deal", "primaryFieldId": "title",
              "fields": [ { "apiName": "title", "displayName": "Title", "required": true },
                          { "apiName": "stage", "displayName": "Stage" },
                          { "apiName": "company_id", "displayName": "Company id" } ] } }
        ]
        """;

    [DockerFact]
    public async Task A_text_field_becomes_a_choice_field_in_place_when_its_values_fit()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Crm, ct);
        var won = await Create(app, "deals", new { title = "A", stage = "won" }, ct);
        await Create(app, "deals", new { title = "B", stage = "Lost" }, ct);
        await Create(app, "deals", new { title = "C", stage = "" }, ct);

        const string Convert = """
            [ { "op": "create", "type": "choiceSet", "value": { "apiName": "deal_stage", "displayName": "Stage",
                "options": [ { "value": "won", "label": "Won" }, { "value": "lost", "label": "Lost" } ] } },
              { "op": "update", "type": "field", "target": "deals.stage", "value": { "type": "choice", "choiceSetId": "deal_stage" } } ]
            """;
        var refused = await Draft(app, Convert, ct);
        var preview = await app.JsonAsync(HttpMethod.Get, "/_model/draft/preview", ct);
        var issue = preview.GetProperty("issues").EnumerateArray().Single(i => i.GetProperty("code").GetString() == "conversion-invalid-values");
        issue.GetProperty("message").GetString().Should().Contain("1 of 2").And.Contain("'Lost'", "the blank one is no value");
        await app.JsonAsync(HttpMethod.Post, "/_model/draft/publish", ct, new { expectedHash = refused }, HttpStatusCode.UnprocessableEntity);
        await app.JsonAsync(HttpMethod.Post, "/_model/draft/discard", ct, new { expectedHash = refused }, HttpStatusCode.NoContent);

        // Fix the record under the live model, then the same conversion goes through.
        var lost = (await app.JsonAsync(HttpMethod.Get, "/_records/deals", ct)).GetProperty("items").EnumerateArray()
            .Single(r => r.GetProperty("title").GetString() == "B");
        await app.JsonAsync(HttpMethod.Patch, $"/_records/deals/{lost.GetProperty("id").GetString()}", ct, new { stage = "lost" });
        await app.PublishAsync(Convert, ct);

        (await app.JsonAsync(HttpMethod.Get, $"/_records/deals/{won}", ct)).GetProperty("stage").GetString().Should().Be("won", "the field kept its values");
        (await app.SendAsync(HttpMethod.Post, "/_records/deals", new { title = "D", stage = "maybe" }, ct)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "it is a choice now");

        // And on to multi-choice: each value becomes a one-item list.
        await app.PublishAsync("""[ { "op": "update", "type": "field", "target": "deals.stage", "value": { "type": "multiChoice" } } ]""", ct);
        (await app.JsonAsync(HttpMethod.Get, $"/_records/deals/{won}", ct)).GetProperty("stage").EnumerateArray()
            .Select(v => v.GetString()).Should().Equal("won");
    }

    [DockerFact]
    public async Task A_new_lookup_takes_over_the_ids_an_old_text_field_held()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Crm, ct);
        var acme = await Create(app, "companies", new { name = "Acme" }, ct);
        var good = await Create(app, "deals", new { title = "Good", company_id = acme.ToUpperInvariant() }, ct);
        var bad = await Create(app, "deals", new { title = "Bad", company_id = "acme" }, ct);

        // Without copyFrom, a required replacement says where the values went and how to bring them.
        var hint = await Draft(app, """
            [ { "op": "delete", "type": "field", "target": "deals.company_id" },
              { "op": "create", "type": "relationship", "value": { "apiName": "company", "sourceTableId": "deals", "targetTableId": "companies", "required": true } } ]
            """, ct);
        var validation = await app.JsonAsync(HttpMethod.Post, "/_model/draft/validate", ct);
        validation.GetProperty("issues").EnumerateArray().Single(i => i.GetProperty("code").GetString() == "required-without-data")
            .GetProperty("message").GetString().Should().Contain("replaces the deleted 'company_id'").And.Contain("copyFrom");
        await app.JsonAsync(HttpMethod.Post, "/_model/draft/discard", ct, new { expectedHash = hint }, HttpStatusCode.NoContent);

        // With it, the ids that name a company move; the one that does not is reported, not invented.
        var hash = await Draft(app, """
            [ { "op": "delete", "type": "field", "target": "deals.company_id" },
              { "op": "create", "type": "relationship", "value": { "apiName": "company", "sourceTableId": "deals", "targetTableId": "companies",
                  "inverseApiName": "deals", "copyFrom": "company_id" } } ]
            """, ct);
        var preview = await app.JsonAsync(HttpMethod.Get, "/_model/draft/preview", ct);
        preview.GetProperty("canPublish").GetBoolean().Should().BeTrue();
        preview.GetProperty("issues").EnumerateArray().Single(i => i.GetProperty("code").GetString() == "copy-partial")
            .GetProperty("message").GetString().Should().Contain("1 of 2").And.Contain("'acme'");
        await app.JsonAsync(HttpMethod.Post, "/_model/draft/publish", ct, new { expectedHash = hash });

        var moved = await app.JsonAsync(HttpMethod.Get, $"/_records/deals/{good}", ct);
        moved.GetProperty("company").GetString().Should().Be(acme);
        moved.GetProperty("version").GetInt32().Should().Be(2, "moving a value is a change to the record");
        (await app.JsonAsync(HttpMethod.Get, $"/_records/deals/{bad}", ct)).GetProperty("company").ValueKind
            .Should().Be(JsonValueKind.Null);
        (await app.JsonAsync(HttpMethod.Get, $"/_records/companies/{acme}/deals", ct)).GetProperty("items").EnumerateArray()
            .Select(d => d.GetProperty("title").GetString()).Should().Equal("Good");
    }

    [DockerFact]
    public async Task CopyFrom_must_name_a_live_field_whose_values_could_fit()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Crm, ct);
        var hash = (await app.JsonAsync(HttpMethod.Get, "/_model", ct)).GetProperty("hash").GetString();
        var applied = await app.JsonAsync(HttpMethod.Post, "/_model/draft/changes", ct, new
        {
            expectedHash = hash,
            operations = AppHarness.Operations("""
                [ { "op": "create", "type": "field", "target": "deals", "value": { "apiName": "won", "displayName": "Won", "type": "boolean", "copyFrom": "stage" } },
                  { "op": "create", "type": "relationship", "value": { "apiName": "owner", "sourceTableId": "deals", "targetTableId": "companies", "copyFrom": "owner_id" } } ]
                """),
        });
        applied.GetProperty("issues").EnumerateArray().Where(i => i.GetProperty("severity").GetString() == "error")
            .Select(i => i.GetProperty("code").GetString()).Should().BeEquivalentTo(["copy-from-incompatible", "copy-from-unknown"]);
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
