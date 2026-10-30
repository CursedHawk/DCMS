using Dcms.IntegrationTests.Tenancy;
using Npgsql;

namespace Dcms.IntegrationTests.Functions;

/// <summary>
/// ADR 0029: a deployment's content, and what its deployer vouched for, is frozen once it leaves
/// Building. The guarantee is a database trigger, so it is tested against the database, as the
/// owner, with raw SQL — the path no application check would stand in.
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class DeploymentFreezeTests(AdminApiFixture fixture)
{
    [DockerFact]
    public async Task A_live_deployment_keeps_its_build_but_may_change_status()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);

        var id = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        await ExecAsync(conn, $"""
            INSERT INTO functions.deployments ("Id", "TenantId", "EnvironmentId", "InstanceId", "Number", "CommitSha", "Status",
                                               "Manifest", "Bundles", "VouchedPermissions", "CreatedAt")
            VALUES ('{id}', '{tenant}', '{tenant}', '{Guid.NewGuid()}', 1, 'abc', 'Building', '{"{}"}', '{"{}"}', '{"{}"}', now())
            """, ct);

        // The build fills a Building row in place.
        await ExecAsync(conn, $"""
            UPDATE functions.deployments SET "Bundles" = '{"{\"hello\":\"export default 1\"}"}',
                "VouchedPermissions" = ARRAY['plugin:dynamic-apps:data-read'] WHERE "Id" = '{id}'
            """, ct);
        await ExecAsync(conn, $"""UPDATE functions.deployments SET "Status" = 'Active' WHERE "Id" = '{id}'""", ct);

        foreach (var set in new[]
                 {
                     """ "Bundles" = '{}' """,
                     """ "Manifest" = '{"functions":[]}' """,
                     """ "CommitSha" = 'def' """,
                     """ "VouchedPermissions" = ARRAY['plugin:functions:deploy'] """,
                 })
        {
            var rewrite = () => ExecAsync(conn, $"""UPDATE functions.deployments SET {set} WHERE "Id" = '{id}'""", ct);
            (await rewrite.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23000");
        }

        // Ready is built, so frozen too, but what it is vouched for is set when someone deploys it.
        var ready = Guid.NewGuid();
        await ExecAsync(conn, $"""
            INSERT INTO functions.deployments ("Id", "TenantId", "EnvironmentId", "InstanceId", "Number", "CommitSha", "Status",
                                               "Manifest", "Bundles", "VouchedPermissions", "CreatedAt")
            VALUES ('{ready}', '{tenant}', '{tenant}', '{Guid.NewGuid()}', 1, 'abc', 'Ready', '{"{}"}', '{"{}"}', '{"{}"}', now())
            """, ct);
        var rebuild = () => ExecAsync(conn, $"""UPDATE functions.deployments SET "Bundles" = '{"{\"x\":1}"}' WHERE "Id" = '{ready}'""", ct);
        (await rebuild.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23000");
        await ExecAsync(conn, $"""
            UPDATE functions.deployments SET "VouchedPermissions" = ARRAY['plugin:functions:deploy'], "Status" = 'Active' WHERE "Id" = '{ready}'
            """, ct);
        await ExecAsync(conn, $"""DELETE FROM functions.deployments WHERE "Id" = '{ready}'""", ct);

        // Superseding is a status change, not a content change; purge may delete.
        await ExecAsync(conn, $"""UPDATE functions.deployments SET "Status" = 'Superseded' WHERE "Id" = '{id}'""", ct);
        await ExecAsync(conn, $"""DELETE FROM functions.deployments WHERE "Id" = '{id}'""", ct);
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
