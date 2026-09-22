using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Npgsql;

namespace Dcms.IntegrationTests.Hardening;

/// <summary>
/// ADR 0015 phase 1. <see cref="RlsIsolationTests"/> proves the policy against
/// <c>dcms_rls</c>, a read-only role that exists to be a test subject; this proves the
/// enforcement target the services will actually connect as — <c>dcms_app</c>,
/// <c>NOBYPASSRLS</c> with DML — behaves in all three states it will be used in, writes
/// included.
///
/// <para>The third state is the new one. <c>platform_scope</c> is what replaces
/// <c>IgnoreQueryFilters()</c> for the paths that legitimately cross tenants: under a
/// non-owner role, ignoring the EF filter widens nothing, so the outbox dispatchers, the
/// publish worker and tenant resolution itself need a way to say "all tenants" that is
/// visible at the call site. If that policy is missing, every one of those paths returns
/// empty — which looks like a data bug, days after the deploy that caused it.</para>
/// </summary>
[Collection(AdminApiCollection.Name)]
public class AppRoleIsolationTests(AdminApiFixture fixture)
{
    private const string AppRole = "dcms_app";
    private const string AppPassword = "dcms-app-test";

    [DockerFact]
    public async Task The_runtime_role_sees_one_tenant_all_tenants_or_none_and_nothing_else()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        // The Testcontainer does not run infra/postgres/init/06-app-role.sh, so the role and
        // its grants are created here. The policies themselves are already on the tables:
        // RlsConfigurator applied them at fixture startup, which is the thing under test.
        await using (var owner = new NpgsqlConnection(fixture.PostgresConnectionString))
        {
            await owner.OpenAsync(ct);
            await ExecAsync(owner, $$"""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{{AppRole}}') THEN
                        CREATE ROLE {{AppRole}} LOGIN PASSWORD '{{AppPassword}}' NOSUPERUSER NOBYPASSRLS;
                    END IF;
                END $$;
                GRANT USAGE ON SCHEMA tenancy TO {{AppRole}};
                GRANT SELECT, INSERT, UPDATE, DELETE ON tenancy.tenant_memberships TO {{AppRole}};
                """, ct);
        }

        var tenantA = await CreateTenantAsync(client, "app-a-" + Guid.NewGuid().ToString("N")[..8], ct);
        var tenantB = await CreateTenantAsync(client, "app-b-" + Guid.NewGuid().ToString("N")[..8], ct);

        await using var app = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.PostgresConnectionString)
        {
            Username = AppRole,
            Password = AppPassword,
        }.ConnectionString);
        await app.OpenAsync(ct);

        // Neither GUC set. This is what a request that lost its tenant looks like, and it is
        // the whole point of the change: empty, not everything.
        await ScopeAsync(app, tenantId: null, platform: false, ct);
        (await TenantIdsAsync(app, ct)).Should().BeEmpty();

        // One tenant at a time.
        await ScopeAsync(app, tenantA, platform: false, ct);
        (await TenantIdsAsync(app, ct)).Should().BeEquivalentTo([tenantA]);

        await ScopeAsync(app, tenantB, platform: false, ct);
        (await TenantIdsAsync(app, ct)).Should().BeEquivalentTo([tenantB]);

        // The deliberate widening, which has to reach both.
        await ScopeAsync(app, tenantId: null, platform: true, ct);
        (await TenantIdsAsync(app, ct)).Should().Contain([tenantA, tenantB]);
    }

    [DockerFact]
    public async Task The_runtime_role_cannot_write_a_row_belonging_to_another_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        await using (var owner = new NpgsqlConnection(fixture.PostgresConnectionString))
        {
            await owner.OpenAsync(ct);
            await ExecAsync(owner, $$"""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{{AppRole}}') THEN
                        CREATE ROLE {{AppRole}} LOGIN PASSWORD '{{AppPassword}}' NOSUPERUSER NOBYPASSRLS;
                    END IF;
                END $$;
                GRANT USAGE ON SCHEMA tenancy TO {{AppRole}};
                GRANT SELECT, INSERT, UPDATE, DELETE ON tenancy.tenant_memberships TO {{AppRole}};
                """, ct);
        }

        var mine = await CreateTenantAsync(client, "app-w-" + Guid.NewGuid().ToString("N")[..8], ct);
        var theirs = await CreateTenantAsync(client, "app-x-" + Guid.NewGuid().ToString("N")[..8], ct);

        await using var app = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.PostgresConnectionString)
        {
            Username = AppRole,
            Password = AppPassword,
        }.ConnectionString);
        await app.OpenAsync(ct);
        await ScopeAsync(app, mine, platform: false, ct);

        // WITH CHECK is the half that a USING-only policy would leave open: a scoped-in
        // caller could still write a row addressed to somebody else.
        var write = async () => await InsertMembershipAsync(app, theirs, ct);
        await write.Should().ThrowAsync<PostgresException>()
            .Where(e => e.SqlState == PostgresErrorCodes.InsufficientPrivilege);

        // The same insert against its own tenant goes through, so the refusal above is the
        // policy and not a missing grant.
        await InsertMembershipAsync(app, mine, ct);
    }

    private static async Task<Guid> CreateTenantAsync(HttpClient client, string slug, CancellationToken ct)
    {
        var owner = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Headers = { { "X-Test-Sub", Guid.NewGuid().ToString() }, { "X-Test-Roles", "SuperAdmin" } },
            Content = JsonContent.Create(new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }),
        };
        var response = await client.SendAsync(request, ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.GetProperty("tenantId").GetGuid();
    }

    /// <summary>Both GUCs at once, so a leftover from the previous assertion cannot be
    /// mistaken for the thing being asserted.</summary>
    private static async Task ScopeAsync(NpgsqlConnection conn, Guid? tenantId, bool platform, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.tenant_id', @t, false), set_config('app.scope', @s, false)";
        cmd.Parameters.AddWithValue("t", tenantId?.ToString() ?? string.Empty);
        cmd.Parameters.AddWithValue("s", platform ? "platform" : string.Empty);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertMembershipAsync(NpgsqlConnection conn, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenancy.tenant_memberships ("Id", "TenantId", "UserId", "Email", "CreatedAt")
            VALUES (@id, @tenant, @user, @email, now())
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("user", Guid.NewGuid());
        cmd.Parameters.AddWithValue("email", $"{Guid.NewGuid():N}@dcms.test");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<Guid>> TenantIdsAsync(NpgsqlConnection conn, CancellationToken ct)
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
