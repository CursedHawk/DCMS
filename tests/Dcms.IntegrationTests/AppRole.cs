namespace Dcms.IntegrationTests;

/// <summary>
/// ADR 0015's runtime roles, for fixtures that boot a service the way it runs in a deploy:
/// <c>dcms_app</c> (<c>NOBYPASSRLS</c>) for the services that read tenant data, and
/// <c>dcms_identity</c> for identity. Grant after the owner has migrated, as the migrate jobs do;
/// this is the part <c>06-app-role.sh</c> and <c>07-identity-role.sh</c> play.
/// </summary>
public static class AppRole
{
    private const string Password = "dcms-app-test";

    /// <summary>Kept in step with SCHEMAS in infra/postgres/init/06-app-role.sh.</summary>
    private const string Schemas =
        "tenancy plugins cms media sites search analytics chat visitors ai forms audit social notifications dataprotection edge platform";

    public static string ConnectionString(string ownerConnectionString) =>
        new Npgsql.NpgsqlConnectionStringBuilder(ownerConnectionString)
        {
            Username = "dcms_app",
            Password = Password,
        }.ConnectionString;

    public static async Task GrantAsync(string ownerConnectionString)
    {
        // Behind the audit maintenance lock: admin-api's worker makes its first pass at startup,
        // and a GRANT over the audit schema racing its DDL fails with "tuple concurrently
        // updated". The lock is the connection's, released as it closes.
        var sql = new System.Text.StringBuilder($$"""
            SELECT pg_advisory_lock({{Dcms.Shared.Data.PostgresAdvisoryLock.AuditMaintenanceLockKey}});
            DO $$ BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dcms_app') THEN
                    CREATE ROLE dcms_app LOGIN PASSWORD '{{Password}}' NOSUPERUSER NOBYPASSRLS;
                END IF;
            END $$;
            """);
        foreach (var schema in Schemas.Split(' '))
        {
            sql.AppendLine($"""
                GRANT USAGE ON SCHEMA "{schema}" TO dcms_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA "{schema}" TO dcms_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA "{schema}" TO dcms_app;
                """);
        }
        // The migrate boot made these before the role existed, so it could not grant them; in a
        // deploy postgres-bootstrap runs first and AuditSchemaConfigurator grants them itself.
        sql.AppendLine("""
            GRANT EXECUTE ON FUNCTION audit.ensure_partitions(int) TO dcms_app;
            GRANT EXECUTE ON FUNCTION audit.drop_sealed_partition(date) TO dcms_app;
            """);

        await ExecuteAsync(ownerConnectionString, sql.ToString());
    }

    private const string IdentityPassword = "dcms-identity-test";

    /// <summary>identity's own runtime role, <c>dcms_identity</c> (ADR 0015 phase 5).</summary>
    public static string IdentityConnectionString(string ownerConnectionString) =>
        new Npgsql.NpgsqlConnectionStringBuilder(ownerConnectionString)
        {
            Username = "dcms_identity",
            Password = IdentityPassword,
        }.ConnectionString;

    /// <summary>Kept in step with infra/postgres/init/07-identity-role.sh.</summary>
    public static Task GrantIdentityAsync(string ownerConnectionString) => ExecuteAsync(ownerConnectionString, $$"""
        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dcms_identity') THEN
                CREATE ROLE dcms_identity LOGIN PASSWORD '{{IdentityPassword}}' NOSUPERUSER NOBYPASSRLS;
            END IF;
        END $$;
        GRANT USAGE ON SCHEMA identity, dataprotection, audit TO dcms_identity;
        GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity, dataprotection TO dcms_identity;
        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA identity, dataprotection TO dcms_identity;
        GRANT SELECT, INSERT ON audit.audit_outbox TO dcms_identity;
        """);

    private static async Task ExecuteAsync(string ownerConnectionString, string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
