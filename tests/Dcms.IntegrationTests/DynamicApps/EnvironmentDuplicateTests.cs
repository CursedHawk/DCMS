using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;

namespace Dcms.IntegrationTests.DynamicApps;

/// <summary>
/// Duplicating an environment with its Dynamic Apps records (ADR 0028): the copy job brings the
/// application and, when asked, every record under new ids, with lookups and unique keys pointing
/// at the copy's own records. The two environments then live apart.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class EnvironmentDuplicateTests(ContentFlowFixture fixture)
{
    private const string Crm = """
        [ { "op": "create", "type": "table", "value": { "apiName": "companies", "displayName": "Company", "primaryFieldId": "name",
              "fields": [ { "apiName": "name", "displayName": "Name", "required": true } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "deals", "displayName": "Deal", "primaryFieldId": "title",
              "fields": [ { "apiName": "title", "displayName": "Title", "required": true },
                          { "apiName": "contact", "displayName": "Contact", "type": "email", "unique": true } ] } },
          { "op": "create", "type": "relationship", "value": { "apiName": "company", "sourceTableId": "deals",
              "targetTableId": "companies", "inverseApiName": "deals", "onDelete": "restrict" } } ]
        """;

    [DockerFact]
    public async Task A_duplicate_takes_the_records_under_new_ids_and_keeps_their_links()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Crm, ct);
        var acme = (await app.JsonAsync(HttpMethod.Post, "/_records/companies", ct, new { name = "Acme" }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await app.JsonAsync(HttpMethod.Post, "/_records/deals", ct, new { title = "Big", contact = "ada@example.com", company = acme }, HttpStatusCode.Created);

        var created = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/environments", app.Owner, app.Tenant,
            body: new { name = "staging", copy = new[] { "dynamic-apps-records" } }), ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
        var staging = $"{app.Tenant}/staging";

        async Task<List<JsonElement>> RecordsAsync(string target, string table)
        {
            var res = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, $"/api/admin/plugins/crm/_records/{table}", app.Owner, target), ct);
            return res.IsSuccessStatusCode
                ? (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items").EnumerateArray().ToList()
                : [];
        }
        // The copy job runs on this fixture's job lanes.
        List<JsonElement> deals = [];
        for (var i = 0; i < 150 && deals.Count == 0; i++)
        {
            deals = await RecordsAsync(staging, "deals");
            if (deals.Count == 0) await Task.Delay(200, ct);
        }
        deals.Should().ContainSingle();
        var copies = await RecordsAsync(staging, "companies");
        copies.Should().ContainSingle();
        var stagingAcme = copies[0].GetProperty("id").GetGuid();
        stagingAcme.Should().NotBe(acme, "rows get ids of their own");
        deals[0].GetRawText().Should().Contain(stagingAcme.ToString(), "the lookup names the copy's company").And.NotContain(acme.ToString());

        // The unique key came along: the copy refuses the same contact twice.
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/plugins/crm/_records/deals", app.Owner, staging,
            body: new { title = "Again", contact = "ADA@example.com" }), ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        // And they live apart from here on.
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/plugins/crm/_records/deals", app.Owner, staging,
            body: new { title = "Staging only" }), ct)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await RecordsAsync(app.Tenant, "deals")).Should().ContainSingle();
    }
}
