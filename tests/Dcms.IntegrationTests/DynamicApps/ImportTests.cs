using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;

namespace Dcms.IntegrationTests.DynamicApps;

/// <summary>
/// Bulk import (ADR 0021): CSV or rows into one table, checked in full and written all or
/// nothing, lookups by id or by the target's name, behind its own permission — and the same
/// operation for the assistant, which names an attached file the console turns into text.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class ImportTests(ContentFlowFixture fixture)
{
    private const string Model = """
        [
          { "op": "create", "type": "choiceSet", "value": { "apiName": "tier", "displayName": "Tier",
              "options": [ { "value": "gold", "label": "Gold" }, { "value": "silver", "label": "Silver" } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "companies", "displayName": "Company", "primaryFieldId": "name",
              "fields": [ { "apiName": "name", "displayName": "Name", "required": true, "unique": true } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "contacts", "displayName": "Contact", "primaryFieldId": "email",
              "fields": [ { "apiName": "email", "displayName": "Email", "type": "email", "required": true, "unique": true },
                          { "apiName": "full_name", "displayName": "Full name" },
                          { "apiName": "vip", "displayName": "VIP", "type": "boolean" },
                          { "apiName": "score", "displayName": "Score", "type": "integer" },
                          { "apiName": "tier", "displayName": "Tier", "type": "choice", "choiceSetId": "tier" } ] } },
          { "op": "create", "type": "relationship", "value": { "apiName": "company", "sourceTableId": "contacts", "targetTableId": "companies",
              "inverseApiName": "contacts" } }
        ]
        """;

    [DockerFact]
    public async Task A_csv_is_checked_in_full_then_imported_with_lookups_by_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Model, ct);
        (await ImportAsync(app, "companies", new { csv = "name\nAcme\n\"Globex, Inc.\"\n" }, dryRun: false, ct)).GetProperty("created").GetInt32().Should().Be(2);

        const string csv = """
            Email;Full name;VIP;score;Tier;company
            ada@example.com;"Lovelace; Ada";yes;10;Gold;acme
            bob@example.com;Bob;no;;silver;"Globex, Inc."
            """;
        var dry = await ImportAsync(app, "contacts", new { csv }, dryRun: true, ct);
        dry.GetProperty("errorCount").GetInt32().Should().Be(0, dry.ToString());
        dry.GetProperty("rows").GetInt32().Should().Be(2);
        (await app.JsonAsync(HttpMethod.Get, "/_records/contacts", ct)).GetProperty("total").GetInt32().Should().Be(0, "a dry run writes nothing");

        (await ImportAsync(app, "contacts", new { csv }, dryRun: false, ct)).GetProperty("created").GetInt32().Should().Be(2);
        var ada = (await app.JsonAsync(HttpMethod.Get, "/_records/contacts?sort=email&expand=company", ct)).GetProperty("items")[0];
        ada.GetProperty("full_name").GetString().Should().Be("Lovelace; Ada", "a quoted cell keeps its separator");
        ada.GetProperty("vip").GetBoolean().Should().BeTrue();
        ada.GetProperty("score").GetInt32().Should().Be(10);
        ada.GetProperty("tier").GetString().Should().Be("gold", "an option matches without regard to case");
        ada.GetProperty("company").GetProperty("name").GetString().Should().Be("Acme", "the lookup was named, not given by id");
    }

    [DockerFact]
    public async Task Names_that_differ_only_in_case_are_told_apart_by_their_exact_spelling()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Model, ct);
        // A unique field compares exactly, so both may exist.
        (await ImportAsync(app, "companies", new { rows = new[] { new { name = "Alder S.R.L." }, new { name = "Alder S.r.l." } } }, dryRun: false, ct))
            .GetProperty("created").GetInt32().Should().Be(2);

        var bad = await ImportAsync(app, "contacts", new { rows = new[] { new { email = "c@example.com", company = "ALDER S.R.L." } } }, dryRun: true, ct);
        bad.GetProperty("errors")[0].GetProperty("message").GetString().Should().Contain("more than one record", "neither is spelled so");

        (await ImportAsync(app, "contacts", new { rows = new[] { new { email = "a@example.com", company = "Alder S.R.L." },
            new { email = "b@example.com", company = "Alder S.r.l." } } }, dryRun: false, ct)).GetProperty("created").GetInt32().Should().Be(2);
        var items = (await app.JsonAsync(HttpMethod.Get, "/_records/contacts?sort=email&expand=company", ct)).GetProperty("items");
        items.EnumerateArray().Select(i => i.GetProperty("company").GetProperty("name").GetString()).Should().Equal("Alder S.R.L.", "Alder S.r.l.");
    }

    [DockerFact]
    public async Task One_bad_row_imports_nothing_and_every_problem_is_reported_by_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Model, ct);
        await app.JsonAsync(HttpMethod.Post, "/_records/contacts", ct, new { email = "taken@example.com" }, HttpStatusCode.Created);

        var result = await ImportAsync(app, "contacts", new
        {
            rows = new object[]
            {
                new { email = "fine@example.com" },
                new { email = "taken@example.com" },
                new { email = "twice@example.com", company = "Nobody Ltd" },
                new { email = "twice@example.com", vip = "maybe" },
                new { email = "fine@example.com" },
                new { email = "ghost@example.com", company = Guid.NewGuid().ToString() },
            },
        }, dryRun: false, ct);
        result.GetProperty("created").GetInt32().Should().Be(0);
        result.GetProperty("errors").EnumerateArray().Select(e => $"{e.GetProperty("row").GetInt32()}:{e.GetProperty("field").GetString()}")
            .Should().BeEquivalentTo(["2:_record", "3:company", "4:vip", "5:_record", "6:company"],
                "taken in the table, twice in the import, and a lookup id with no record behind it");
        (await app.JsonAsync(HttpMethod.Get, "/_records/contacts", ct)).GetProperty("total").GetInt32().Should().Be(1, "not even the good row");

        (await app.SendAsync(HttpMethod.Post, "/_records/contacts/import", new { csv = "nickname\nx\n" }, ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "a column naming no field is refused before any row");
        (await app.SendAsync(HttpMethod.Post, "/_records/companies/import", new { csv = "name\nGlobex, Inc.\n" }, ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "an unquoted separator would cut the name short");
    }

    [DockerFact]
    public async Task Import_has_its_own_permission_and_the_assistant_imports_through_the_same_operation()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Model, ct);
        var writer = await app.AddMemberAsync(["plugin:dynamic-apps:data-read", "plugin:dynamic-apps:data-write"], ct);
        (await app.SendAsync(HttpMethod.Post, "/_records/companies/import", new { csv = "name\nAcme\n" }, ct, writer))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "creating records one by one is not importing them");
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Put, $"/api/admin/plugins/instances/{app.InstanceId}", app.Owner, app.Tenant,
            body: new { aiToolsEnabled = true }), ct)).IsSuccessStatusCode.Should().BeTrue();

        var check = await Contract(app, "ValidateImport", new { table = "companies", csv = "name\nAcme\nInitech\n" }, ct);
        check.GetProperty("rows").GetInt32().Should().Be(2);
        check.GetProperty("created").GetInt32().Should().Be(0);
        (await Contract(app, "ImportRecords", new { table = "companies", csv = "name\nAcme\nInitech\n" }, ct)).GetProperty("created").GetInt32().Should().Be(2);

        var unsent = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/contracts/dynamic-apps.records@1/ImportRecords?instance=crm&plane=ai",
            app.Owner, app.Tenant, body: new { table = "companies", csvFile = "companies.csv" }), ct);
        unsent.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await unsent.Content.ReadAsStringAsync(ct)).Should().Contain("companies.csv", "the server says the console did not send the file");
    }

    [DockerFact]
    public async Task An_import_past_the_request_ceiling_runs_as_a_tracked_job_still_all_or_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Model, ct);

        // 1,100 rows: past the 1,000 a request may carry, so refused before ADR 0024 and now queued
        // and run by a job worker. Kept just past the ceiling: the volume is not what is under test.
        var csv = "name\n" + string.Join("\n", Enumerable.Range(1, 1100).Select(i => $"Company {i}"));
        var queued = await app.JsonAsync(HttpMethod.Post, "/_records/companies/import", ct, new { csv }, HttpStatusCode.Accepted);
        var job = await FinishedAsync(app, queued.GetProperty("jobId").GetGuid(), ct);

        job.GetProperty("status").GetString().Should().Be("Succeeded", job.ToString());
        job.GetProperty("result").GetProperty("created").GetInt32().Should().Be(1100);
        job.GetProperty("done").GetInt64().Should().Be(job.GetProperty("total").GetInt64());
        // Where it came from (added by the runtime for every plugin job) and where its rows are.
        var links = job.GetProperty("links").EnumerateArray()
            .Select(l => (l.GetProperty("rel").GetString(), l.GetProperty("path").GetString(), l.GetProperty("name").GetString())).ToList();
        links.Should().Contain(("source", "/apps/crm", "CRM"));
        links.Should().Contain(("result", "/apps/crm/records?table=companies", "companies"));
        (await app.JsonAsync(HttpMethod.Get, "/_records/companies?pageSize=1", ct)).GetProperty("total").GetInt32().Should().Be(1100);

        // One bad row among 1,100 more: the job reports it, and writes none of them.
        var bad = "name\n" + string.Join("\n", Enumerable.Range(1, 1100).Select(i => i == 700 ? "Company 1" : $"Other {i}"));
        var rejected = await app.JsonAsync(HttpMethod.Post, "/_records/companies/import", ct, new { csv = bad }, HttpStatusCode.Accepted);
        var failed = await FinishedAsync(app, rejected.GetProperty("jobId").GetGuid(), ct);
        failed.GetProperty("result").GetProperty("created").GetInt32().Should().Be(0);
        failed.GetProperty("result").GetProperty("errorCount").GetInt32().Should().Be(1);
        (await app.JsonAsync(HttpMethod.Get, "/_records/companies?pageSize=1", ct)).GetProperty("total").GetInt32().Should().Be(1100);
    }

    [DockerFact]
    public async Task A_bulk_delete_past_the_request_ceiling_runs_as_a_tracked_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Model, ct);
        // 520 rows fit one request; deleting all 520 does not (the bulk ceiling is 500).
        var csv = "name\n" + string.Join("\n", Enumerable.Range(1, 520).Select(i => $"Company {i}"));
        await app.JsonAsync(HttpMethod.Post, "/_records/companies/import", ct, new { csv });
        var ids = new List<Guid>();
        for (var page = 1; ids.Count < 520; page++)
        {
            var items = (await app.JsonAsync(HttpMethod.Get, $"/_records/companies?pageSize=200&page={page}", ct)).GetProperty("items");
            ids.AddRange(items.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        }

        var queued = await app.JsonAsync(HttpMethod.Post, "/_records/companies/bulk-delete", ct, new { ids }, HttpStatusCode.Accepted);
        var job = await FinishedAsync(app, queued.GetProperty("jobId").GetGuid(), ct);

        job.GetProperty("status").GetString().Should().Be("Succeeded", job.ToString());
        job.GetProperty("result").GetProperty("affected").GetInt32().Should().Be(520);
        (await app.JsonAsync(HttpMethod.Get, "/_records/companies?pageSize=1", ct)).GetProperty("total").GetInt32().Should().Be(0);
    }

    private static async Task<JsonElement> FinishedAsync(AppHarness app, Guid jobId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        JsonElement last = default;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var res = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, $"/api/admin/jobs/{jobId}", app.Owner, app.Tenant), ct);
            res.EnsureSuccessStatusCode();
            last = (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("job");
            if (last.GetProperty("status").GetString() is "Succeeded" or "Failed" or "Canceled") return last;
            await Task.Delay(250, ct);
        }
        throw new TimeoutException($"Job {jobId} never finished: {last}");
    }

    private static async Task<JsonElement> ImportAsync(AppHarness app, string table, object body, bool dryRun, CancellationToken ct) =>
        await app.JsonAsync(HttpMethod.Post, $"/_records/{table}/import?dryRun={dryRun.ToString().ToLowerInvariant()}", ct, body);

    private static async Task<JsonElement> Contract(AppHarness app, string op, object input, CancellationToken ct)
    {
        var res = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/contracts/dynamic-apps.records@1/{op}?instance=crm&plane=ai",
            app.Owner, app.Tenant, body: input), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
