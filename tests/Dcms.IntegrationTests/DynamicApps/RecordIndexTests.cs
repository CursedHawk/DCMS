using System.Net;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;

namespace Dcms.IntegrationTests.DynamicApps;

/// <summary>
/// The Postgres indexes behind sortable and filterable fields (RecordIndexes): the automation worker
/// makes them for what is published and drops them with the field, queries answer exactly as before,
/// and the planner uses them.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class RecordIndexTests(ContentFlowFixture fixture)
{
    private const string Model = """
        [
          { "op": "create", "type": "choiceSet", "value": { "apiName": "stage", "displayName": "Stage",
              "options": [ { "value": "lead", "label": "Lead" }, { "value": "won", "label": "Won" } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "deals", "displayName": "Deal", "primaryFieldId": "title",
              "fields": [
                { "apiName": "title", "displayName": "Title", "required": true, "sortable": true },
                { "apiName": "amount", "displayName": "Amount", "type": "decimal", "sortable": true, "filterable": true },
                { "apiName": "stage", "displayName": "Stage", "type": "choice", "choiceSetId": "stage", "filterable": true },
                { "apiName": "closes", "displayName": "Closes", "type": "date", "sortable": true },
                { "apiName": "won", "displayName": "Won", "type": "boolean", "sortable": true } ] } }
        ]
        """;

    [DockerFact]
    public async Task Sortable_and_filterable_fields_are_indexed_used_and_dropped_with_the_field()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Model, ct);
        var table = (await app.JsonAsync(HttpMethod.Get, "/_model/published", ct)).GetProperty("config").GetProperty("tables")[0].GetProperty("id").GetString()!;

        // title 2 (+1 search: it is the primary field and nothing is searchable), amount 2, stage 1, closes 2; the boolean none.
        await WaitAsync(async () => await IndexesAsync(table, ct) == 8, ct, "the worker makes the indexes");

        var rows = Enumerable.Range(1, 300).Select(i => new
        {
            title = $"Deal {i:000}", amount = (i * 37) % 1000, stage = i % 3 == 0 ? "won" : "lead",
            closes = new DateOnly(2026, 1, 1).AddDays(i % 200).ToString("yyyy-MM-dd"), won = i % 2 == 0,
        }).ToList();
        rows.Add(new { title = "Zed 100% done", amount = 1, stage = "lead", closes = "2026-01-01", won = false });
        rows.Add(new { title = "Zed 100 percent", amount = 1, stage = "lead", closes = "2026-01-01", won = false });
        await app.JsonAsync(HttpMethod.Post, "/_records/deals/import?dryRun=false", ct, new { rows });
        await Analyze(ct);
        var before = await ScansAsync(table, ct);

        var byAmount = await Items(app, "/_records/deals?sort=-amount&pageSize=5&select=amount", ct);
        byAmount.Select(d => d.GetProperty("amount").GetDecimal()).Should().Equal(rows.Select(r => (decimal)r.amount).OrderDescending().Take(5));
        var byTitle = await Items(app, "/_records/deals?sort=title&pageSize=3&select=title", ct);
        byTitle.Select(d => d.GetProperty("title").GetString()).Should().Equal("Deal 001", "Deal 002", "Deal 003");
        var range = await app.JsonAsync(HttpMethod.Post, "/_records/deals/query", ct, new
        {
            filter = new { and = new object[] { new { field = "closes", op = "gte", value = "2026-07-01" }, new { field = "stage", op = "eq", value = "won" } } },
            sort = new[] { new { field = "closes" } }, pageSize = 200,
        });
        var expected = rows.Where(r => string.CompareOrdinal(r.closes, "2026-07-01") >= 0 && r.stage == "won").ToList();

        // Search: as many as contain the term, and a literal % is a literal.
        (await app.JsonAsync(HttpMethod.Get, "/_records/deals?search=deal%2001&pageSize=50", ct)).GetProperty("total").GetInt32()
            .Should().Be(rows.Count(r => r.title.Contains("deal 01", StringComparison.OrdinalIgnoreCase)));
        (await Items(app, "/_records/deals?search=100%25&select=title", ct)).Select(d => d.GetProperty("title").GetString()).Should().Equal("Zed 100% done");
        // The title's trigram index (its primary field, nothing being searchable). Whether the planner
        // reads it is a matter of size: with 300 rows the table index is cheaper, so that is checked on vps1.
        (await ScalarAsync($"""
            SELECT count(*) FROM pg_indexes WHERE indexname LIKE 'ix_da_%' AND indexdef LIKE '%{table}%'
              AND indexdef LIKE '%USING gin (lower(("Data" ->> %' AND indexdef LIKE '%gin_trgm_ops%'
            """, ct)).Should().Be(1);
        range.GetProperty("total").GetInt32().Should().Be(expected.Count);
        range.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("closes").GetString()).Should().BeInAscendingOrder();

        // Index statistics reach pg_stat_user_indexes when admin-api's pooled connections flush them
        // (at most once a second, later while idle), so wait for them rather than read them once.
        await WaitAsync(async () => await ScansAsync(table, ct) > before, ct, "the planner reads these indexes");

        // A field that goes takes its indexes with it.
        var hash = (await app.JsonAsync(HttpMethod.Get, "/_model", ct)).GetProperty("hash").GetString();
        var draft = (await app.JsonAsync(HttpMethod.Post, "/_model/draft/changes", ct, new
        {
            expectedHash = hash,
            operations = AppHarness.Operations("""[ { "op": "delete", "type": "field", "target": "deals.amount" } ]"""),
        })).GetProperty("draft").GetProperty("hash").GetString();
        await app.JsonAsync(HttpMethod.Post, "/_model/draft/publish", ct, new { expectedHash = draft });
        await WaitAsync(async () => await IndexesAsync(table, ct) == 6, ct, "the worker drops the field's indexes");
    }

    [DockerFact]
    public async Task A_total_stops_counting_at_the_cap_and_says_so()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Model, ct);
        var cap = Dcms.Plugins.DynamicApps.Data.RecordService.MaxCount;
        for (var from = 0; from <= cap; from += 1000)
        {
            var rows = Enumerable.Range(from, Math.Min(1000, cap + 1 - from)).Select(i => new { title = $"Deal {i:00000}", amount = i % 10 }).ToList();
            await app.JsonAsync(HttpMethod.Post, "/_records/deals/import?dryRun=false", ct, new { rows });
        }

        var all = await app.JsonAsync(HttpMethod.Get, "/_records/deals?pageSize=10", ct);
        all.GetProperty("total").GetInt32().Should().Be(cap);
        all.GetProperty("totalCapped").GetBoolean().Should().BeTrue("one more matches than the count went to");
        var few = await app.JsonAsync(HttpMethod.Post, "/_records/deals/query", ct, new { filter = new { field = "amount", op = "eq", value = 3 } });
        few.GetProperty("total").GetInt32().Should().Be((cap + 1) / 10);
        few.GetProperty("totalCapped").GetBoolean().Should().BeFalse("under the cap the count is exact");
    }

    private static async Task<List<JsonElement>> Items(AppHarness app, string path, CancellationToken ct) =>
        (await app.JsonAsync(HttpMethod.Get, path, ct)).GetProperty("items").EnumerateArray().ToList();

    private async Task<long> IndexesAsync(string table, CancellationToken ct) =>
        await ScalarAsync($"SELECT count(*) FROM pg_indexes WHERE schemaname = 'apps' AND indexname LIKE 'ix_da_%' AND indexdef LIKE '%{table}%'", ct);

    private async Task<long> ScansAsync(string table, CancellationToken ct) =>
        await ScalarAsync($"""
            SELECT coalesce(sum(s.idx_scan), 0) FROM pg_stat_user_indexes s JOIN pg_indexes i ON i.indexname = s.indexrelname
            WHERE i.schemaname = 'apps' AND i.indexname LIKE 'ix_da_%' AND i.indexdef LIKE '%{table}%'
            """, ct);

    private async Task Analyze(CancellationToken ct) => await ScalarAsync("SELECT 0 FROM (SELECT 1) x; ANALYZE apps.records", ct);

    private async Task<long> ScalarAsync(string sql, CancellationToken ct)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0);
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, CancellationToken ct, string what)
    {
        for (var i = 0; i < 120; i++)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(500, ct);
        }
        throw new Xunit.Sdk.XunitException($"Timed out: {what}.");
    }
}
