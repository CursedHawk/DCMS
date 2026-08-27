using Dcms.IntegrationTests.Tenancy;
using Npgsql;

namespace Dcms.IntegrationTests.Observability;

/// <summary>
/// The reporting surface Grafana reads, and the boundary around it.
///
/// <para><b>Why the boundary is tested rather than assumed.</b> Grafana always lets a user with
/// a Postgres datasource write raw SQL — there is no setting that turns that off. So the only
/// thing standing between a Grafana account and <c>identity."AspNetUsers"</c> is what
/// <c>dcms_grafana</c> is granted in the database. A test that only checked the views were
/// readable would pass just as happily against a role with full access, which is the exact
/// misconfiguration worth catching.</para>
/// </summary>
[Collection(AdminApiCollection.Name)]
public class ReportingRoleTests(AdminApiFixture fixture)
{
    private const string ReportingRole = "dcms_grafana";
    private const string ReportingPassword = "dcms-grafana-test";

    [DockerFact]
    public async Task Reporting_role_reads_obs_views_and_nothing_else()
    {
        var ct = TestContext.Current.CancellationToken;

        // The Testcontainer does not run infra/postgres/init/03-observability-role.sh, so
        // create the role here exactly as that script does. The *views* are not created here:
        // ObservabilityViewConfigurator made them during admin-api's startup, which is the
        // path production uses and therefore the path worth exercising.
        await using (var owner = new NpgsqlConnection(fixture.PostgresConnectionString))
        {
            await owner.OpenAsync(ct);
            await ExecAsync(owner, $"""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{ReportingRole}') THEN
                        CREATE ROLE {ReportingRole} LOGIN PASSWORD '{ReportingPassword}'
                            NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
                    END IF;
                END $$;
                GRANT USAGE ON SCHEMA obs TO {ReportingRole};
                GRANT SELECT ON ALL TABLES IN SCHEMA obs TO {ReportingRole};
                """, ct);

            var created = await ViewNamesAsync(owner, ct);

            // Nine, not thirteen. This fixture boots admin-api alone, and identity owns its own
            // migrations, so identity's tables do not exist here — which takes out exactly the
            // four views that read them. That is not a defect in the views: the configurator
            // skips an undefined table with a warning and re-runs on every start, so on a real
            // deployment they appear as soon as identity has migrated. The four are named
            // rather than counted, so this test fails loudly if a *different* view starts
            // failing and the arithmetic happens to still work out.
            string[] needsIdentity =
                ["v_users_summary", "v_user_activity", "v_user_demographics", "v_growth_daily"];

            created.Should().NotContain(needsIdentity);
            created.Should().Contain("view_version",
                "the last statement having run is what says the configurator got to the end");
            created.Should().HaveCount(9,
                "every view that does not depend on identity should have been created");
        }

        var reporting = new NpgsqlConnectionStringBuilder(fixture.PostgresConnectionString)
        {
            Username = ReportingRole,
            Password = ReportingPassword,
        }.ConnectionString;

        await using var grafana = new NpgsqlConnection(reporting);
        await grafana.OpenAsync(ct);

        // Every view, not a sample: a view the role cannot read is a dashboard panel that is
        // empty for a reason nobody can see from the panel.
        var names = await ViewNamesAsync(grafana, ct);
        names.Should().NotBeEmpty();
        foreach (var view in names)
        {
            var read = async () => await ScalarAsync(grafana, $"SELECT count(*) FROM obs.\"{view}\"", ct);
            await read.Should().NotThrowAsync($"obs.{view} must be readable by {ReportingRole}");
        }

        // The other half. These are the tables the views deliberately do not expose: the raw
        // audit rows, the identity table with its email addresses and password hashes, and the
        // AI settings with their Vault ciphertext.
        foreach (var table in new[]
                 {
                     "identity.\"AspNetUsers\"",
                     "audit.audit_events",
                     "ai.tenant_ai_settings",
                     "tenancy.tenants",
                 })
        {
            var read = async () => await ScalarAsync(grafana, $"SELECT count(*) FROM {table}", ct);
            await read.Should().ThrowAsync<PostgresException>(
                $"{ReportingRole} must not be able to reach {table} — Grafana permits raw SQL");
        }
    }

    private static async Task<List<string>> ViewNamesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT table_name FROM information_schema.views WHERE table_schema = 'obs' ORDER BY table_name";
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
