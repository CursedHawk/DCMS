using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Npgsql;

namespace Dcms.IntegrationTests.Hardening;

/// <summary>
/// Raw-SQL Row-Level Security isolation test (ADR 0005). Bypasses EF entirely:
/// connects as the least-privilege <c>dcms_rls</c> role (no BYPASSRLS) and proves
/// the <c>tenant_isolation</c> policy keyed on the <c>app.tenant_id</c> GUC only
/// exposes the matching tenant's rows — the defense-in-depth backstop beneath the
/// EF query filters.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class RlsIsolationTests(AdminApiFixture fixture)
{
    private const string RlsRole = "dcms_rls";
    private const string RlsPassword = "dcms-rls-test";

    private static HttpRequestMessage CreateTenantReq(string slug, Guid owner) =>
        new(HttpMethod.Post, "/api/admin/tenants")
        {
            Headers = { { "X-Test-Sub", Guid.NewGuid().ToString() }, { "X-Test-Roles", "SuperAdmin" } },
            Content = JsonContent.Create(new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }),
        };

    [DockerFact]
    public async Task Rls_role_sees_only_the_tenant_set_in_app_tenant_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        // The Testcontainer doesn't run infra/postgres/init/01-rls.sql, so create
        // the least-privilege role + grants here (RLS itself is already applied by
        // RlsConfigurator on startup). Run as the owner.
        await using (var owner = new NpgsqlConnection(fixture.PostgresConnectionString))
        {
            await owner.OpenAsync(ct);
            await ExecAsync(owner, $$"""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{{RlsRole}}') THEN
                        CREATE ROLE {{RlsRole}} LOGIN PASSWORD '{{RlsPassword}}' NOSUPERUSER NOBYPASSRLS;
                    END IF;
                END $$;
                GRANT USAGE ON SCHEMA tenancy TO {{RlsRole}};
                GRANT SELECT ON tenancy.tenant_memberships TO {{RlsRole}};
                """, ct);
        }

        // Two tenants; each gets an owner membership row stamped with its tenant id.
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        var tenantA = await CreateTenantAsync(client, "rls-a-" + Guid.NewGuid().ToString("N")[..8], ownerA, ct);
        var tenantB = await CreateTenantAsync(client, "rls-b-" + Guid.NewGuid().ToString("N")[..8], ownerB, ct);

        var rlsConnString = new NpgsqlConnectionStringBuilder(fixture.PostgresConnectionString)
        {
            Username = RlsRole,
            Password = RlsPassword,
        }.ConnectionString;

        await using var rls = new NpgsqlConnection(rlsConnString);
        await rls.OpenAsync(ct);

        // Scoped to tenant A → sees A's membership, never B's.
        await SetTenantAsync(rls, tenantA, ct);
        (await DistinctTenantIdsAsync(rls, ct)).Should().BeEquivalentTo([tenantA]);

        // Scoped to tenant B → sees B's, never A's.
        await SetTenantAsync(rls, tenantB, ct);
        (await DistinctTenantIdsAsync(rls, ct)).Should().BeEquivalentTo([tenantB]);

        // No tenant set → the policy exposes nothing.
        await SetTenantAsync(rls, null, ct);
        (await DistinctTenantIdsAsync(rls, ct)).Should().BeEmpty();
    }

    private static async Task<Guid> CreateTenantAsync(HttpClient client, string slug, Guid owner, CancellationToken ct)
    {
        var res = await client.SendAsync(CreateTenantReq(slug, owner), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.GetProperty("tenantId").GetGuid();
    }

    private static async Task SetTenantAsync(NpgsqlConnection conn, Guid? tenantId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.tenant_id', @t, false)";
        cmd.Parameters.AddWithValue("t", tenantId?.ToString() ?? string.Empty);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<Guid>> DistinctTenantIdsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT \"TenantId\" FROM tenancy.tenant_memberships";
        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetGuid(0));
        }
        return ids;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
