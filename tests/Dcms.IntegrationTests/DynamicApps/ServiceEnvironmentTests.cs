using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.IntegrationTests.Cms;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Shared.Data.Rls;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.DynamicApps;

/// <summary>
/// A service environment's tables used from another environment through a tables link (ADR 0028):
/// the call runs as the service environment, and only for the tables and verbs the link grants.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class ServiceEnvironmentTests(ContentFlowFixture fixture)
{
    private const string Crm = """
        [ { "op": "create", "type": "table", "value": { "apiName": "companies", "displayName": "Company", "primaryFieldId": "name",
              "fields": [ { "apiName": "name", "displayName": "Name", "required": true } ] } } ]
        """;

    [DockerFact]
    public async Task Another_environment_reads_a_service_environments_table_through_a_link_and_nothing_more()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Crm, ct);
        await app.JsonAsync(HttpMethod.Post, "/_records/companies", ct, new { name = "Acme" }, HttpStatusCode.Created);

        async Task<JsonElement> CreateAsync(object body)
        {
            var res = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/environments", app.Owner, app.Tenant, body: body), ct);
            res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync(ct));
            return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        }
        var sales = await CreateAsync(new { name = "sales", service = true, copy = new[] { "dynamic-apps-records" } });
        sales.GetProperty("purpose").GetString().Should().Be("service");
        var development = await CreateAsync(new { name = "development" });
        var (salesId, devId) = (sales.GetProperty("id").GetGuid(), development.GetProperty("id").GetGuid());
        var tenantId = (await (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/environments", app.Owner, app.Tenant), ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("workspaceId").GetGuid();

        // Both copies are jobs on this fixture's lanes.
        for (var i = 0; i < 150; i++)
        {
            var res = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/plugins/crm/_records/companies", app.Owner, $"{app.Tenant}/sales"), ct);
            var dev = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/plugins/crm/_model", app.Owner, $"{app.Tenant}/development"), ct);
            if (res.IsSuccessStatusCode && (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items").GetArrayLength() == 1
                && dev.IsSuccessStatusCode) break;
            await Task.Delay(200, ct);
        }

        // Production's people and pipeline do not change for a service.
        var list = await (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/environments", app.Owner, app.Tenant), ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);
        list.GetProperty("environments").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "production")
            .GetProperty("promotesFrom").EnumerateArray().Select(x => x.GetGuid()).Should().Equal(devId);

        async Task<IDynamicAppsRecords> LinkedAsync()
        {
            var scope = fixture.Admin.Services.CreateScope();
            using var rls = RlsScope.Environment(tenantId, devId);
            var factory = scope.ServiceProvider.GetRequiredService<PluginContextFactory>();
            var instance = await factory.FindAnyInstanceAsync(tenantId, "crm", ct);
            var context = await factory.CreateAsync(tenantId, "dynamic-apps", instance, PluginActor.Anonymous, ct);
            return await context.Contracts.LinkedAsync<IDynamicAppsRecords>("sales", "crm", ct);
        }

        // No link yet: nothing.
        await FluentActions.Awaiting(LinkedAsync).Should().ThrowAsync<ContractAccessDeniedException>();

        // Tables sales really has, as it offers them.
        var offers = await (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, $"/api/admin/environments/{salesId}/offers", app.Owner, app.Tenant), ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);
        offers.EnumerateArray().Single(o => o.GetProperty("slug").GetString() == "crm").GetProperty("tables").EnumerateArray()
            .Select(t => t.GetString()).Should().Equal("companies");
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/environments/{devId}/links", app.Owner, app.Tenant,
            body: new { kind = "tables", targetEnvironmentId = salesId, targetKey = "crm", grants = new[] { "companies:read", "ghosts:read" } }), ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var linked = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/environments/{devId}/links", app.Owner, app.Tenant,
            body: new { kind = "tables", targetEnvironmentId = salesId, targetKey = "crm", grants = new[] { "companies:read" } }), ct);
        linked.StatusCode.Should().Be(HttpStatusCode.Created, await linked.Content.ReadAsStringAsync(ct));

        var records = await LinkedAsync();
        var page = await records.QueryRecordsAsync(new TableQuery("companies"), ct);
        page.Items.Select(r => r["name"]!.GetValue<string>()).Should().Equal("Acme");
        await FluentActions.Awaiting(() => records.CreateRecordAsync(new RecordCreate("companies", new JsonObject { ["name"] = "Globex" }), ct))
            .Should().ThrowAsync<ContractAccessDeniedException>("the link grants reading only");
        // Nor another table through a lookup: an expansion, or a filter on lookup.field.
        await FluentActions.Awaiting(() => records.QueryRecordsAsync(new TableQuery("companies", new RecordQuery { Expand = ["owner"] }), ct))
            .Should().ThrowAsync<ContractAccessDeniedException>();
        await FluentActions.Awaiting(() => records.QueryRecordsAsync(new TableQuery("companies",
                new RecordQuery { Filter = new RecordFilter { Field = "owner.name", Op = "eq", Value = "x" } }), ct))
            .Should().ThrowAsync<ContractAccessDeniedException>().WithMessage("*reaches another through a lookup*");
    }

    [DockerFact]
    public async Task A_flow_calls_an_operation_of_another_environments_app_through_a_contracts_link_and_nothing_more()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        var billing = (await (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/environments", app.Owner, app.Tenant,
            body: new { name = "billing", service = true, empty = true }), ct)).Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        var inBilling = $"{app.Tenant}/billing";
        var invoices = await JsonOk(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/plugins/instances", app.Owner, inBilling,
            body: new { pluginId = "blog", slug = "invoices", name = "Invoices", config = "{}" }), ct), ct);
        var item = await JsonOk(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/content", app.Owner, inBilling,
            body: new { pluginInstanceId = invoices.GetProperty("id").GetGuid(), contentType = "post", slug = "march", data = new { title = "March invoices" } }), ct), ct);
        await JsonOk(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/content/{item.GetProperty("id").GetGuid()}/publish", app.Owner, inBilling), ct), ct);
        var production = (await JsonOk(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/environments", app.Owner, app.Tenant), ct), ct))
            .GetProperty("workspaceId").GetGuid();

        // What billing offers is listed for whoever sets the link up.
        var offered = await JsonOk(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, $"/api/admin/environments/{billing}/offers", app.Owner, app.Tenant), ct), ct);
        offered.EnumerateArray().Single(i => i.GetProperty("slug").GetString() == "invoices").GetProperty("operations").EnumerateArray()
            .Select(o => o.GetProperty("grant").GetString()).Should().Contain(["blog.posts/Get", "blog.posts/List"]);

        await app.PublishAsync("""
            [ { "op": "create", "type": "flow", "value": { "apiName": "invoice", "displayName": "Invoice",
                "steps": [ { "id": "get", "action": "environment.contract.call@1",
                             "input": { "environment": "billing", "app": "invoices", "contract": "blog.posts", "operation": "Get", "input": { "slug": "march" } } } ] } } ]
            """, ct);
        async Task<JsonElement> RunAsync()
        {
            var runId = (await app.JsonAsync(HttpMethod.Post, "/_automation/flows/invoice/run", ct, new { input = new { } }, HttpStatusCode.Accepted))
                .GetProperty("runId").GetString();
            for (var i = 0; i < 120; i++)
            {
                var run = await app.JsonAsync(HttpMethod.Get, $"/_automation/runs/{runId}", ct);
                if (run.GetProperty("run").GetProperty("status").GetString() is "succeeded" or "failed") return run;
                await Task.Delay(250, ct);
            }
            throw new Xunit.Sdk.XunitException("Timed out waiting for the automation worker.");
        }
        Task<HttpResponseMessage> Link(params string[] grants) => app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/environments/{production}/links",
            app.Owner, app.Tenant, body: new { kind = "contracts", targetEnvironmentId = billing, targetKey = "invoices", grants }), ct);

        var refused = await RunAsync();
        refused.GetProperty("run").GetProperty("status").GetString().Should().Be("failed");
        refused.GetProperty("run").GetProperty("error").GetString().Should().Contain("no link");

        // Only operations billing's app really has, and offers to other environments: never a
        // Dynamic Apps model change or an all-table record write (production's crm, from billing).
        (await Link("blog.posts/Delete")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/environments/{billing}/links", app.Owner, app.Tenant,
            body: new { kind = "contracts", targetEnvironmentId = production, targetKey = "crm", grants = new[] { "dynamic-apps.records/DeleteRecord" } }), ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Link("forms.submissions/List")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // A link granting List does not grant Get.
        var listOnly = await JsonOk(await Link("blog.posts/List"), ct);
        (await RunAsync()).GetProperty("run").GetProperty("error").GetString().Should().Contain("does not grant blog.posts/Get");
        await JsonOk(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Delete, $"/api/admin/environments/{production}/links/{listOnly.GetProperty("id").GetGuid()}",
            app.Owner, app.Tenant), ct), ct);

        await JsonOk(await Link("blog.posts/Get"), ct);
        var ran = await RunAsync();
        ran.GetProperty("run").GetProperty("status").GetString().Should().Be("succeeded", ran.ToString());
        ran.ToString().Should().Contain("March invoices", "billing's post, read as billing");
    }

    private static async Task<JsonElement> JsonOk(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        res.IsSuccessStatusCode.Should().BeTrue(text);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }
}
