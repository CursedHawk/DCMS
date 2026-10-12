extern alias AdminApiApp;
extern alias PlatformApiApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AdminApiApp::Dcms.AdminApi.Tenancy.Usage;
using PlatformApiApp::Dcms.PlatformApi.Reporting;
using Dcms.IntegrationTests.Cms;
using Dcms.IntegrationTests.DynamicApps;
using Dcms.Shared.Data.Rls;

namespace Dcms.IntegrationTests.Usage;

/// <summary>
/// The usage snapshot: every category measured for the asking tenant and nobody else, the
/// Recalculate rate limit, and who may read what.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class TenantUsageTests(ContentFlowFixture fixture)
{
    private const string Model = """
        [ { "op": "create", "type": "table", "value": { "apiName": "companies", "displayName": "Company", "primaryFieldId": "name",
              "fields": [ { "apiName": "name", "displayName": "Name", "required": true } ] } } ]
        """;

    private static readonly Guid SuperAdmin = Guid.NewGuid();

    [DockerFact]
    public async Task A_tenant_is_measured_by_category_and_never_counts_another_tenants_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var mine = await TenantWithRecordsAsync(3, ct);
        await TenantWithRecordsAsync(5, ct);

        var res = await mine.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/usage/recalculate", mine.Owner, mine.Tenant), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(ct));
        var usage = await res.Content.ReadFromJsonAsync<JsonElement>(ct);

        var categories = usage.GetProperty("categories").EnumerateArray().ToList();
        var keys = categories.Select(c => c.GetProperty("key").GetString()).ToList();
        keys.Should().Contain(["dynamicApps", "plugins", "workspace"]);
        usage.GetProperty("totalBytes").GetInt64().Should().Be(categories.Sum(c => c.GetProperty("bytes").GetInt64()));
        usage.GetProperty("quotaBytes").GetInt64().Should().Be(5L * 1024 * 1024 * 1024);

        // Exactly this tenant's three records; the other tenant's five do not leak in.
        var records = Item(categories, "dynamicApps", "records");
        records.GetProperty("rows").GetInt64().Should().Be(3);
        records.GetProperty("kind").GetString().Should().Be("database");
        Item(categories, "plugins", "plugin:dynamic-apps").GetProperty("label").GetString().Should().NotBe("plugin:dynamic-apps");

        // Sorted biggest first, at both levels.
        categories.Select(c => c.GetProperty("bytes").GetInt64()).Should().BeInDescendingOrder();

        // A second click straight away is refused; the snapshot stays readable.
        var again = await mine.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/usage/recalculate", mine.Owner, mine.Tenant), ct);
        again.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        again.Headers.RetryAfter.Should().NotBeNull();

        // The console's tenant page reads the same snapshot through obs.v_tenant_usage.
        await using (var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = PlatformOverviewEndpoints.TenantSelect + "\nWHERE i.tenant_slug = @slug";
            cmd.Parameters.AddWithValue("slug", mine.Tenant);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            (await reader.ReadAsync(ct)).Should().BeTrue();
            reader.GetInt64(reader.GetOrdinal("usage_bytes")).Should().Be(usage.GetProperty("totalBytes").GetInt64());
            JsonDocument.Parse(reader.GetString(reader.GetOrdinal("categories"))).RootElement.GetArrayLength()
                .Should().Be(categories.Count);
        }

        var read = await mine.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/usage", mine.Owner, mine.Tenant), ct);
        (await read.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("measuredAt").GetDateTimeOffset()
            .Should().Be(usage.GetProperty("measuredAt").GetDateTimeOffset());
    }

    [DockerFact]
    public async Task Usage_needs_tenant_settings_and_the_tenant_routes_need_the_console()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        var tenantId = await TenantIdAsync(app.Tenant, ct);

        var editor = await app.AddMemberAsync(["content:read"], ct);
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/usage", editor, app.Tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/usage/recalculate", editor, app.Tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Before the first measurement there is nothing to show, and that is not an error.
        var before = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/usage", app.Owner, app.Tenant), ct);
        (await before.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("measuredAt").ValueKind.Should().Be(JsonValueKind.Null);

        // The tenant's own owner is not the console.
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/tenants/{tenantId}/usage/recalculate", app.Owner, app.Tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, $"/api/admin/tenants/{tenantId}/directory", app.Owner, app.Tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var measured = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/tenants/{tenantId}/usage/recalculate", SuperAdmin, "", "SuperAdmin"), ct);
        measured.StatusCode.Should().Be(HttpStatusCode.OK, await measured.Content.ReadAsStringAsync(ct));

        var directory = await (await app.Admin.SendAsync(
            AppHarness.Req(HttpMethod.Get, $"/api/admin/tenants/{tenantId}/directory", SuperAdmin, "", "SuperAdmin"), ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);
        directory.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("email").GetString())
            .Should().Contain($"{app.Owner:N}@dcms.test");
        directory.GetProperty("plugins").EnumerateArray().Select(p => p.GetProperty("slug").GetString()).Should().Contain("crm");

        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/tenants/{Guid.NewGuid()}/usage/recalculate", SuperAdmin, "", "SuperAdmin"), ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<AppHarness> TenantWithRecordsAsync(int count, CancellationToken ct)
    {
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Model, ct);
        for (var i = 0; i < count; i++)
        {
            await app.JsonAsync(HttpMethod.Post, "/_records/companies", ct, new { name = $"Company {i}" }, HttpStatusCode.Created);
        }
        return app;
    }

    private async Task<Guid> TenantIdAsync(string slug, CancellationToken ct)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "Id"::uuid FROM tenancy.tenants WHERE "Identifier" = @slug""";
        cmd.Parameters.AddWithValue("slug", slug);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static JsonElement Item(List<JsonElement> categories, string category, string key) =>
        categories.Single(c => c.GetProperty("key").GetString() == category)
            .GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == key);
}

/// <summary>No Docker: the map is checked against the RLS table list at build time.</summary>
public sealed class TenantUsageMapTests
{
    [Fact]
    public void Every_tenant_table_has_a_category_and_no_mapping_is_stale()
    {
        TenantUsageMap.Tables.Keys.Should().BeEquivalentTo(RlsConfigurator.TenantTables);
    }

    [Fact]
    public void Every_category_is_one_the_consoles_know()
    {
        TenantUsageMap.Tables.Values.Select(t => t.Category).Distinct()
            .Should().BeSubsetOf(["media", "sites", "plugins", "dynamicApps", "ai", "audit", "workspace"]);
    }
}
