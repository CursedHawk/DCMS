extern alias AdminApiApp;

using System.Net.Http.Json;
using System.Text.Json;
using AdminApiApp::Dcms.AdminApi.Environments;
using Dcms.IntegrationTests.Cms;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.DynamicApps;

/// <summary>
/// An application's configuration deploys between environments (ADR 0026, 0028) as a published
/// revision in the receiving one, its records untouched.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class EnvironmentDeploymentTests(ContentFlowFixture fixture)
{
    private const string Crm = """
        [ { "op": "create", "type": "table", "value": { "apiName": "companies", "displayName": "Company", "primaryFieldId": "name",
              "fields": [ { "apiName": "name", "displayName": "Name", "required": true } ] } } ]
        """;

    [DockerFact]
    public async Task A_published_application_in_development_is_deployed_to_production()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        async Task<JsonElement> As(HttpMethod method, string url, string tenant, object? body = null)
        {
            var res = await app.Admin.SendAsync(AppHarness.Req(method, url, app.Owner, tenant, body: body), ct);
            res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(ct));
            return res.Content.Headers.ContentLength == 0 ? default : await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        }

        var dev = (await As(HttpMethod.Post, "/api/admin/environments", app.Tenant, new { name = "development" })).GetProperty("id").GetGuid();
        var prod = (await As(HttpMethod.Get, "/api/admin/environments", app.Tenant)).GetProperty("workspaceId").GetGuid();
        var devSlug = $"{app.Tenant}/development";
        // This fixture runs job lanes: the copy job queued with the environment sets it up. Done is
        // the job finished, not its first kind arrived: building the app while it still runs races
        // its later kinds, which write production's application over it.
        for (var i = 0; i < 200; i++)
        {
            var jobs = await As(HttpMethod.Get, "/api/admin/jobs?pageSize=20", app.Tenant);
            if (jobs.GetProperty("items").EnumerateArray().Any(j => j.GetProperty("kind").GetString() == "environments.copy"
                    && j.GetProperty("status").GetString() == "Succeeded"))
            {
                break;
            }
            await Task.Delay(100, ct);
        }
        (await As(HttpMethod.Get, "/api/admin/environments/changes", devSlug)).EnumerateArray()
            .Should().BeEmpty("the copy is complete and is the baseline");

        // The application is built in development.
        var hash = (await As(HttpMethod.Get, "/api/admin/plugins/crm/_model", devSlug)).GetProperty("hash").GetString();
        var draft = await As(HttpMethod.Post, "/api/admin/plugins/crm/_model/draft/changes", devSlug,
            new { expectedHash = hash, operations = AppHarness.Operations(Crm) });
        await As(HttpMethod.Post, "/api/admin/plugins/crm/_model/draft/publish", devSlug,
            new { expectedHash = draft.GetProperty("draft").GetProperty("hash").GetString() });
        var migration = (await As(HttpMethod.Post, "/api/admin/environments/migrations", devSlug, new { name = "CRM companies" }))
            .GetProperty("migrationId").GetGuid();

        var pending = await As(HttpMethod.Get, $"/api/admin/environments/pending?from={dev}", app.Tenant);
        pending[0].GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("id").GetString())
            .Should().Contain("dynamic-apps/crm");

        using (var scope = fixture.Admin.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EnvironmentMigrator>().ApplyAsync(
                await Environments.EnvironmentTarget.LoadAsync(fixture.Admin.Services, prod, prod),
                await Environments.EnvironmentTarget.LoadAsync(fixture.Admin.Services, prod, dev), migration, new Dictionary<string, string>(), new EnvironmentApplier(app.Owner, false), ct);
        }

        var published = await As(HttpMethod.Get, "/api/admin/plugins/crm/_model/published", app.Tenant);
        published.GetProperty("config").GetProperty("tables").EnumerateArray()
            .Select(t => t.GetProperty("apiName").GetString()).Should().Equal("companies");
        published.GetProperty("revision").GetProperty("source").GetString().Should().BeEquivalentTo("import");

        // Once production leaves the application to deployments, it is not published there directly.
        await As(HttpMethod.Put, $"/api/admin/environments/{prod}/policies", app.Tenant,
            new[] { new { kind = "dynamic-apps", itemKey = (string?)null, mode = "managed" } });
        var prodHash = (await As(HttpMethod.Get, "/api/admin/plugins/crm/_model", app.Tenant)).GetProperty("hash").GetString();
        var prodDraft = await As(HttpMethod.Post, "/api/admin/plugins/crm/_model/draft/changes", app.Tenant, new
        {
            expectedHash = prodHash,
            operations = AppHarness.Operations("""[ { "op": "update", "type": "table", "target": "companies", "value": { "displayName": "Firm" } } ]"""),
        });
        var publish = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/plugins/crm/_model/draft/publish", app.Owner, app.Tenant,
            body: new { expectedHash = prodDraft.GetProperty("draft").GetProperty("hash").GetString() }), ct);
        publish.StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
    }
}
