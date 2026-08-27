using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Data.Observability;

/// <summary>
/// Creates the <c>obs</c> reporting schema and re-grants it to the Grafana role, on every
/// admin-api startup.
///
/// <para>A startup configurator rather than a file in <c>infra/postgres/init/</c>, for the
/// reason the runbook already documents about the audit schema: those scripts run only when
/// Postgres initialises an empty data directory, and vps1's cluster was initialised long ago.
/// A view added to an init script would exist on a laptop and nowhere that matters. The same
/// reasoning put <c>AuditSchemaConfigurator</c> and <c>RlsConfigurator</c> here, and this runs
/// immediately after them.</para>
///
/// <para>Every statement is idempotent — <c>CREATE SCHEMA IF NOT EXISTS</c>,
/// <c>CREATE OR REPLACE VIEW</c>, <c>GRANT</c> — so running it on sixty restarts costs sixty
/// no-ops. The grants are re-applied each time on purpose: a view replaced by a later version
/// of this file keeps its grants, but a view created for the first time does not have any,
/// and re-granting unconditionally means a new view is never left readable by nobody.</para>
///
/// <para><b>This does not create the role.</b> Creating a login role means holding its
/// password, and admin-api has no business with a credential it never uses. The role comes
/// from <c>infra/postgres/init/03-observability-role.sh</c> on a fresh cluster, or from one
/// hand-run statement on an existing one. If it is absent, the grants below are skipped with
/// a warning and the views are still created — a missing Grafana is not a reason to fail the
/// startup of the service that runs the platform's migrations.</para>
/// </summary>
public static class ObservabilityViewConfigurator
{
    public const string RoleName = "dcms_grafana";
    public const string SchemaName = "obs";

    public static async Task ApplyAsync(DbContext context, ILogger logger, CancellationToken ct = default)
    {
        await context.Database.ExecuteSqlRawAsync(
            $"CREATE SCHEMA IF NOT EXISTS {SchemaName}", ct);

        foreach (var statement in ObservabilityViews.Statements)
        {
            try
            {
                await context.Database.ExecuteSqlRawAsync(statement, ct);
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
            {
                // undefined_table. Four of these views read identity's tables, and identity
                // owns its own migrations — so on a genuinely fresh cluster this service can
                // reach here before those tables exist. That resolves itself: the configurator
                // re-runs on every start, and the next one finds them. Logged as a warning
                // with the cause named, because four stack traces about a missing relation
                // read like a broken deployment rather than a startup race.
                logger.LogWarning(
                    "Observability view skipped: {Message}. If the table belongs to another "
                    + "service's migrations (identity's, for the user and growth views), this "
                    + "resolves on the next start after that service has migrated. Statement began: {Statement}",
                    ex.MessageText,
                    Excerpt(statement));
            }
            catch (Exception ex)
            {
                // Any other failure — a column renamed by a migration, most likely — must not
                // take out the rest of the reporting surface, and must not take out the startup
                // of the service that owns every migration on the platform. Logged loudly
                // enough to be found, because the symptom otherwise is one dashboard panel that
                // is empty for reasons nobody can see.
                logger.LogError(
                    ex,
                    "Failed to create an observability view. The reporting dashboards that use it will be empty. Statement began: {Statement}",
                    Excerpt(statement));
            }
        }

        var roleExists = await RoleExistsAsync(context, ct);
        if (!roleExists)
        {
            logger.LogWarning(
                "Postgres role {Role} does not exist, so the {Schema} views were created but granted to nobody. "
                + "Grafana's reporting datasource will fail to connect until it is created — see infra/postgres/init/03-observability-role.sh.",
                RoleName,
                SchemaName);
            return;
        }

        await context.Database.ExecuteSqlRawAsync(
            $"""
             GRANT USAGE ON SCHEMA {SchemaName} TO {RoleName};
             GRANT SELECT ON ALL TABLES IN SCHEMA {SchemaName} TO {RoleName};
             ALTER DEFAULT PRIVILEGES IN SCHEMA {SchemaName} GRANT SELECT ON TABLES TO {RoleName};
             """,
            ct);

        logger.LogInformation(
            "Observability views applied (version {Version}) and granted to {Role}.",
            ObservabilityViews.Version,
            RoleName);
    }

    private static async Task<bool> RoleExistsAsync(DbContext context, CancellationToken ct)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT 1 FROM pg_roles WHERE rolname = '{RoleName}'";

        var opened = false;
        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {
            await context.Database.OpenConnectionAsync(ct);
            opened = true;
        }

        try
        {
            return await command.ExecuteScalarAsync(ct) is not null;
        }
        finally
        {
            if (opened)
            {
                await context.Database.CloseConnectionAsync();
            }
        }
    }

    private static string Excerpt(string statement)
    {
        var trimmed = statement.TrimStart();
        var end = trimmed.IndexOf('\n');
        return end < 0 ? trimmed : trimmed[..end];
    }
}
