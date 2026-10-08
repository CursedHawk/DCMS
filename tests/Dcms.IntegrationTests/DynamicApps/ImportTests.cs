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
            },
        }, dryRun: false, ct);
        result.GetProperty("created").GetInt32().Should().Be(0);
        result.GetProperty("errors").EnumerateArray().Select(e => $"{e.GetProperty("row").GetInt32()}:{e.GetProperty("field").GetString()}")
            .Should().BeEquivalentTo(["2:_record", "3:company", "4:vip", "5:_record"], "taken in the table, and twice in the import");
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
