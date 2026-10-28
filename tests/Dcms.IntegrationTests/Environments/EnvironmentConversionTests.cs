extern alias AdminApiApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AdminApiApp::Dcms.AdminApi.Tenancy;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Data.Observability;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Dcms.IntegrationTests.Environments;

/// <summary>
/// ADR 0026's environment tenants fold into their workspace (ADR 0028): the environment keeps its
/// id, its rows are re-keyed to the workspace, its mirrored members become the workspace's with
/// their roles there, and the old columns go.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class EnvironmentConversionTests(AdminApiFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    private static HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string? target = null, object? body = null, string roles = "")
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (roles.Length > 0) req.Headers.Add("X-Test-Roles", roles);
        if (target is not null) EnvironmentTarget.Apply(req, target);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage res, CancellationToken ct)
    {
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(ct));
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    [DockerFact]
    public async Task An_environment_tenant_becomes_an_environment_of_its_workspace()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var owner = Guid.NewGuid();
        var slug = "conv-" + Guid.NewGuid().ToString("N")[..8];
        async Task<Guid> TenantAsync(string s) =>
            (await Ok(await client.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, roles: "SuperAdmin",
                body: new { slug = s, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct), ct))
            .GetProperty("tenantId").GetGuid();
        var workspace = await TenantAsync(slug);
        var environment = await TenantAsync($"{slug}-development");
        await Ok(await client.SendAsync(Req(HttpMethod.Post, "/api/admin/roles", owner, $"{slug}-development",
            new { name = "Developers", permissions = new[] { "content:read" } }), ct), ct);

        // As ADR 0026 left it: two tenants, grouped by WorkspaceId.
        await ExecAsync($$"""
            ALTER TABLE tenancy.tenants
                ADD COLUMN IF NOT EXISTS "WorkspaceId" uuid,
                ADD COLUMN IF NOT EXISTS "EnvironmentName" character varying(32),
                ADD COLUMN IF NOT EXISTS "EnvironmentOrder" integer NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS "PromotesFrom" uuid[] NOT NULL DEFAULT '{}';
            UPDATE tenancy.tenants SET "WorkspaceId" = '{{workspace}}', "EnvironmentName" = 'production', "PromotesFrom" = '{{{environment}}}'
                WHERE "Id" = '{{workspace}}';
            UPDATE tenancy.tenants SET "WorkspaceId" = '{{workspace}}', "EnvironmentName" = 'development', "EnvironmentOrder" = -1
                WHERE "Id" = '{{environment}}';
            INSERT INTO tenancy.domains ("Id","TenantId","EnvironmentId","Hostname","VerificationToken","IsPrimary","CreatedAt")
                VALUES ('{{Guid.NewGuid()}}','{{environment}}','{{environment}}','{{slug}}-dev.example.test','t',false,now());
            -- Another service's table in the same database, keyed by "TenantId" as identity's realms
            -- are: each environment keeps its own row, so it must not be re-keyed.
            CREATE SCHEMA IF NOT EXISTS conversion_probe;
            CREATE TABLE IF NOT EXISTS conversion_probe.realms ("TenantId" uuid PRIMARY KEY);
            INSERT INTO conversion_probe.realms VALUES ('{{workspace}}'), ('{{environment}}');
            INSERT INTO analytics.daily_rollups ("TenantId","Day","SiteId","Type","Path","Count")
                VALUES ('{{workspace}}','2026-10-01','{{Guid.Empty}}','pageview','/',3), ('{{environment}}','2026-10-01','{{Guid.Empty}}','pageview','/',4);
            """, ct);

        // As the migration job runs it: the schema's owner.
        object? probe;
        try
        {
            await using var tenancy = new TenancyDbContext(
                new DbContextOptionsBuilder<TenancyDbContext>().UseNpgsql(fixture.PostgresConnectionString).Options);
            await EnvironmentConversion.ApplyAsync(tenancy, NullLogger.Instance, ct);
            await EnvironmentConversion.ApplyAsync(tenancy, NullLogger.Instance, ct); // and again: a no-op
            await ObservabilityViewConfigurator.ApplyAsync(tenancy, NullLogger.Instance, ct);
            probe = await ScalarAsync($"""SELECT count(*) FROM conversion_probe.realms WHERE "TenantId" = '{environment}'""", ct);
        }
        finally
        {
            // Not left for the RLS coverage scan, which would take it for an unregistered tenant table.
            await ExecAsync("DROP SCHEMA IF EXISTS conversion_probe CASCADE", ct);
        }
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<EnvironmentStore>().Evict(workspace);
        }

        var list = (await Ok(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/environments", owner, slug), ct), ct))
            .GetProperty("environments").EnumerateArray().ToList();
        list.Select(e => (e.GetProperty("name").GetString(), e.GetProperty("id").GetGuid()))
            .Should().Equal(("development", environment), ("production", workspace));
        list[1].GetProperty("promotesFrom").EnumerateArray().Select(x => x.GetGuid()).Should().Equal(environment);

        // Its roles, and the owner's place in them, came along.
        var roles = await Ok(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", owner, $"{slug}/development"), ct), ct);
        roles.EnumerateArray().Select(r => r.GetProperty("name").GetString()).Should().Contain("Developers");
        var mine = await Ok(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/me/tenants", owner), ct), ct);
        mine.EnumerateArray().Select(t => t.GetProperty("slug").GetString()).Should().Contain(slug).And.NotContain($"{slug}-development");

        (await ScalarAsync($"""SELECT count(*) FROM tenancy.tenants WHERE "Id" = '{environment}'""", ct)).Should().Be(0L);
        (await ScalarAsync($"""SELECT "Count" FROM analytics.daily_rollups WHERE "TenantId" = '{workspace}' AND "Day" = '2026-10-01'""", ct))
            .Should().Be(7L, "rollups of the same key are summed");
        probe.Should().Be(1L, "another service's rows keyed by the environment stay its own");
        (await ScalarAsync($"""SELECT count(*) FROM tenancy.tenant_memberships WHERE "TenantId" = '{workspace}'""", ct)).Should().Be(1L, "one member list");
        (await ScalarAsync($"""SELECT count(*) FROM tenancy.domains WHERE "TenantId" = '{workspace}' AND "EnvironmentId" = '{environment}'""", ct)).Should().Be(1L);
        (await ScalarAsync("""SELECT count(*) FROM information_schema.columns WHERE table_schema = 'tenancy' AND table_name = 'tenants' AND column_name = 'WorkspaceId'""", ct))
            .Should().Be(0L);
    }

    private async Task ExecAsync(string sql, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<object?> ScalarAsync(string sql, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync(ct);
    }
}
