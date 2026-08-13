using Testcontainers.PostgreSql;

namespace Dcms.IntegrationTests;

/// <summary>
/// The throwaway Postgres every fixture boots against. EF migrations create their
/// own schemas but cannot create extensions, so the cluster-level setup from
/// infra/postgres/init has to run first — the Search migration builds a GIN index
/// with gin_trgm_ops and fails outright without pg_trgm. The real init script is
/// mounted rather than restated here so tests and deployment cannot drift.
/// </summary>
internal static class TestPostgres
{
    /// <summary>Copied next to the test binary by the csproj; see 00-schemas.sql.</summary>
    private static readonly string InitScript =
        Path.Combine(AppContext.BaseDirectory, "postgres-init", "00-schemas.sql");

    public static PostgreSqlContainer Build() =>
        new PostgreSqlBuilder("postgres:18")
            .WithDatabase("dcms")
            .WithUsername("dcms")
            .WithPassword("dcms-dev")
            // The postgres image runs everything in this directory on first init,
            // against POSTGRES_DB, before it accepts outside connections.
            .WithResourceMapping(new FileInfo(InitScript), "/docker-entrypoint-initdb.d/")
            .Build();
}
