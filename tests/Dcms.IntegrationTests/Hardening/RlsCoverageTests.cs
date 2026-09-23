using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Dcms.IntegrationTests.Hardening;

/// <summary>
/// ARCH-01. <see cref="RlsIsolationTests"/> proves the policy works — on one table. This proves
/// it is <i>there</i>, on every table the registry claims, by asking Postgres rather than the
/// application.
///
/// <para>That distinction is the finding. <c>infra/postgres/init/01-rls.sql</c> grants SELECT on
/// every future table in the tenant schemas to <c>dcms_rls</c>, while the policies are opt-in per
/// table, so a table with a grant and no policy is readable unfiltered across every tenant. The
/// startup log said "applied to N tenant tables", which was the length of a hand-written array;
/// and the apply loop logs a warning and continues when a table fails, so the number was printed
/// either way.</para>
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class RlsCoverageTests(AdminApiFixture fixture)
{
    [DockerFact]
    public async Task Every_registered_tenant_table_is_protected_in_the_catalogue()
    {
        var ct = TestContext.Current.CancellationToken;
        // As the owner, never the app's own connection: these read the catalogue and then drop
        // and restore policies, which is DDL -- and under DCMS_TEST_RLS_ENFORCE the app is
        // dcms_app, which may not.
        await using var db = OwnerContext();

        // The fixture's host has already run this on startup; running it again is the assertion,
        // and it is idempotent by design.
        await RlsConfigurator.AssertAppliedAsync(db, NullLogger.Instance, ct);
    }

    /// <summary>
    /// The mutation check: with the policy gone the assertion must fail, naming that table.
    /// Without this, a verifier that returned "all clear" unconditionally would pass the test
    /// above — which is exactly the shape of the bug it was written for.
    /// </summary>
    [DockerFact]
    public async Task A_table_that_loses_its_policy_fails_the_assertion()
    {
        var ct = TestContext.Current.CancellationToken;
        // As the owner, never the app's own connection: these read the catalogue and then drop
        // and restore policies, which is DDL -- and under DCMS_TEST_RLS_ENFORCE the app is
        // dcms_app, which may not.
        await using var db = OwnerContext();

        // A table nothing else in this collection writes through a policy-sensitive path.
        const string victim = "\"analytics\".\"daily_rollups\"";
        await ExecAsync(db, $"DROP POLICY IF EXISTS tenant_isolation ON {victim}", ct);
        try
        {
            var act = () => RlsConfigurator.AssertAppliedAsync(db, NullLogger.Instance, ct);
            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*analytics.daily_rollups*");
        }
        finally
        {
            // Put it back exactly as ApplyAsync writes it, so the rest of the collection — and a
            // rerun of this file — sees the protected state again.
            await RlsConfigurator.ApplyAsync(db, NullLogger.Instance, ct);
        }

        await RlsConfigurator.AssertAppliedAsync(db, NullLogger.Instance, ct);
    }

    /// <summary>
    /// The partition leg, which is the one that was actually broken. Postgres applies a
    /// partitioned table's policy to queries through the parent; a query aimed straight at a
    /// partition is checked against that partition's own policies, and enabling row security on
    /// the parent enables nothing on the children. 01-rls.sql's ALTER DEFAULT PRIVILEGES grants
    /// dcms_rls SELECT on each new monthly audit partition regardless, so before this fix
    /// `SELECT * FROM audit.audit_events_2026m09` returned every tenant's records to the one
    /// role that exists to prove it cannot.
    /// </summary>
    [DockerFact]
    public async Task An_audit_partition_is_protected_in_its_own_right()
    {
        var ct = TestContext.Current.CancellationToken;
        // As the owner, never the app's own connection: these read the catalogue and then drop
        // and restore policies, which is DDL -- and under DCMS_TEST_RLS_ENFORCE the app is
        // dcms_app, which may not.
        await using var db = OwnerContext();

        var partition = $"audit_events_{DateTime.UtcNow:yyyy}m{DateTime.UtcNow:MM}";
        await ExecAsync(db, $"DROP POLICY IF EXISTS tenant_isolation ON \"audit\".\"{partition}\"", ct);
        try
        {
            var act = () => RlsConfigurator.AssertAppliedAsync(db, NullLogger.Instance, ct);
            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage($"*audit.{partition}*", "a partition is a table of its own to Postgres");
        }
        finally
        {
            await RlsConfigurator.ApplyAsync(db, NullLogger.Instance, ct);
        }

        await RlsConfigurator.AssertAppliedAsync(db, NullLogger.Instance, ct);
    }

    /// <summary>
    /// The other direction: a table that carries a TenantId in the database but appears in
    /// neither list. <c>AssertCoverage</c> catches this at startup from the EF model; this
    /// catches it from the schema, which is where a hand-written migration would put one.
    /// Partitions are excluded — they are covered by their parent's entry, and
    /// <see cref="An_audit_partition_is_protected_in_its_own_right"/> is what holds them.
    /// </summary>
    [DockerFact]
    public async Task No_tenant_carrying_table_is_missing_from_both_lists()
    {
        var ct = TestContext.Current.CancellationToken;
        var known = new HashSet<(string, string)>(RlsConfigurator.TenantTables);
        known.UnionWith(RlsConfigurator.ExemptTables);

        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.table_schema, c.table_name
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
             AND t.table_type = 'BASE TABLE'
            JOIN pg_namespace n ON n.nspname = c.table_schema
            JOIN pg_class pc ON pc.relnamespace = n.oid AND pc.relname = c.table_name
            WHERE c.column_name = 'TenantId'
              AND c.table_schema NOT IN ('pg_catalog', 'information_schema')
              AND NOT EXISTS (SELECT 1 FROM pg_inherits i WHERE i.inhrelid = pc.oid)
            """;

        var unregistered = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var pair = (reader.GetString(0), reader.GetString(1));
            if (!known.Contains(pair))
            {
                unregistered.Add($"{pair.Item1}.{pair.Item2}");
            }
        }

        unregistered.Should().BeEmpty(
            "a table with a TenantId and no entry in RlsConfigurator.TenantTables or ExemptTables " +
            "is readable across every tenant by the dcms_rls role");
    }

    private static Task ExecAsync(DbContext db, string sql, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(sql, ct);

    private TenancyDbContext OwnerContext() => new(
        new DbContextOptionsBuilder<TenancyDbContext>().UseNpgsql(fixture.PostgresConnectionString).Options,
        new NoTenant());

    private sealed class NoTenant : Dcms.Shared.Kernel.Abstractions.ITenantContext
    {
        public Guid? TenantId => null;
        public string? TenantSlug => null;
    }
}
