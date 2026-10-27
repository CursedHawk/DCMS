using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dcms.IntegrationTests.Hardening;

/// <summary>
/// Environments are a dimension of one tenant (ADR 0028): a row of one environment is invisible
/// from another, to the EF filter and to the row-level security policy alike, and a row written
/// with no environment named lands in the one the work is in. Production's environment id is the
/// tenant's, so everything written before environments existed is production's.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class EnvironmentIsolationTests(AdminApiFixture fixture)
{
    private const string RlsRole = "dcms_rls";
    private const string RlsPassword = "dcms-rls-test";

    [DockerFact]
    public async Task A_row_of_one_environment_is_invisible_from_another()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var owner = Guid.NewGuid();
        var slug = "envi-" + Guid.NewGuid().ToString("N")[..8];
        var created = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Headers = { { "X-Test-Sub", Guid.NewGuid().ToString() }, { "X-Test-Roles", "SuperAdmin" } },
            Content = JsonContent.Create(new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }),
        }, ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenant = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tenantId").GetGuid();
        var development = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fixture.PostgresConnectionString))
        {
            await conn.OpenAsync(ct);
            await ExecAsync(conn, $$"""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{{RlsRole}}') THEN
                        CREATE ROLE {{RlsRole}} LOGIN PASSWORD '{{RlsPassword}}' NOSUPERUSER NOBYPASSRLS;
                    END IF;
                END $$;
                GRANT USAGE ON SCHEMA tenancy TO {{RlsRole}};
                GRANT SELECT ON tenancy.tenant_roles TO {{RlsRole}};
                INSERT INTO tenancy.environments ("Id", "TenantId", "Name", "Order", "Purpose", "PromotesFrom", "MergesOwnChangesOnly", "DeleteWhenMerged", "CreatedAt")
                VALUES ('{{development}}', '{{tenant}}', 'development', -1, 'Stage', '{}', false, false, now());
                """, ct);
        }

        // Every tenant starts with its production, whose id is its own.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            using var rls = RlsScope.Tenant(tenant);
            (await db.Environments.IgnoreTenantFilter().Where(e => e.TenantId == tenant).Select(e => e.Name).ToListAsync(ct))
                .Should().BeEquivalentTo(["production", "development"]);
            (await db.Environments.IgnoreTenantFilter().SingleAsync(e => e.TenantId == tenant && e.Name == "production", ct))
                .Id.Should().Be(tenant);
        }

        // A role written in development, with no environment named: stamped with the one in force.
        using (var scope = fixture.Factory.Services.CreateScope())
        using (RlsScope.Environment(tenant, development))
        {
            var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            db.TenantRoles.Add(new TenantRole { Id = Guid.NewGuid(), TenantId = tenant, Name = "Developers" });
            await db.SaveChangesAsync(ct);
        }

        async Task<List<string>> RolesAsync(Guid? environment)
        {
            using var scope = fixture.Factory.Services.CreateScope();
            using var rls = environment is { } e ? RlsScope.Environment(tenant, e) : RlsScope.Tenant(tenant);
            return await scope.ServiceProvider.GetRequiredService<TenancyDbContext>().TenantRoles.IgnoreTenantFilter()
                .Where(r => r.TenantId == tenant).Select(r => r.Name).ToListAsync(ct);
        }
        (await RolesAsync(development)).Should().Equal("Developers");
        (await RolesAsync(null)).Should().BeEquivalentTo(["Owner", "Member"], "a tenant block is its production");
        (await RolesAsync(tenant)).Should().BeEquivalentTo(["Owner", "Member"]);

        // The database draws the same line on its own, for a role that bypasses nothing.
        var rlsConnection = new NpgsqlConnectionStringBuilder(fixture.PostgresConnectionString) { Username = RlsRole, Password = RlsPassword }.ConnectionString;
        await using var limited = new NpgsqlConnection(rlsConnection);
        await limited.OpenAsync(ct);
        async Task<List<string>> SeenAsync(Guid? environment)
        {
            await ExecAsync(limited, $"SELECT set_config('app.tenant_id', '{tenant}', false), set_config('app.environment_id', '{environment?.ToString() ?? ""}', false)", ct);
            await using var cmd = limited.CreateCommand();
            cmd.CommandText = """SELECT "Name" FROM tenancy.tenant_roles""";
            var names = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
            return names;
        }
        (await SeenAsync(development)).Should().Equal("Developers");
        (await SeenAsync(null)).Should().BeEquivalentTo(["Owner", "Member"], "no environment named is production");
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
