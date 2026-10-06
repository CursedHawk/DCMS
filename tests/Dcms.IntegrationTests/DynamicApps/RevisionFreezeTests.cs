using Dcms.IntegrationTests.Tenancy;
using Npgsql;

namespace Dcms.IntegrationTests.DynamicApps;

/// <summary>
/// ADR 0021: a configuration revision's content is frozen once it leaves Draft. The guarantee is
/// a database trigger, so it is tested against the database, as the owner, with raw SQL — the
/// path no application check would stand in.
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class RevisionFreezeTests(AdminApiFixture fixture)
{
    [DockerFact]
    public async Task A_published_revision_keeps_its_snapshot_but_may_change_status()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);

        var id = Guid.NewGuid();
        await ExecAsync(conn, $"""
            INSERT INTO apps.revisions ("Id", "TenantId", "AppId", "Number", "Status", "Source", "Snapshot", "Hash",
                                        "SchemaVersion", "CreatedAt", "UpdatedAt")
            VALUES ('{id}', '{Guid.NewGuid()}', '{Guid.NewGuid()}', 1, 'Draft', 'Human', '{"{}"}', 'h1', 1, now(), now())
            """, ct);

        // A draft is edited in place.
        await ExecAsync(conn, $"""UPDATE apps.revisions SET "Snapshot" = '{"{\"tables\":[]}"}', "Hash" = 'h2' WHERE "Id" = '{id}'""", ct);
        await ExecAsync(conn, $"""UPDATE apps.revisions SET "Status" = 'Published' WHERE "Id" = '{id}'""", ct);

        var rewrite = () => ExecAsync(conn, $"""UPDATE apps.revisions SET "Snapshot" = '{"{}"}' WHERE "Id" = '{id}'""", ct);
        (await rewrite.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23000");

        var rehash = () => ExecAsync(conn, $"""UPDATE apps.revisions SET "Hash" = 'h3' WHERE "Id" = '{id}'""", ct);
        await rehash.Should().ThrowAsync<PostgresException>();

        // Superseding a published revision is a status change, not a content change.
        await ExecAsync(conn, $"""UPDATE apps.revisions SET "Status" = 'Superseded' WHERE "Id" = '{id}'""", ct);
        await ExecAsync(conn, $"""DELETE FROM apps.revisions WHERE "Id" = '{id}'""", ct);
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
