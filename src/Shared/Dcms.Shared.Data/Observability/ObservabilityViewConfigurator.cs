using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Data.Observability;

/// <summary>
/// Creates the <c>obs</c> reporting schema and re-grants it to every reader role, on each
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
/// <para><b>This does not create the roles.</b> Creating a login role means holding its
/// password, and admin-api has no business with a credential it never uses. They come from
/// <c>infra/postgres/init/03-observability-role.sh</c> and <c>04-platform-role.sh</c> on a
/// fresh cluster, and from <c>postgres-bootstrap</c> re-running those scripts on an existing
/// one. If a role is absent, its grants are skipped with a warning and the views are still
/// created — a missing reader is not a reason to fail the startup of the service that runs
/// the platform's migrations.</para>
///
/// <para>There are two readers and they want the same thing for different reasons. Grafana
/// reads <c>obs</c> because a dashboard editor can always type raw SQL, so the boundary has to
/// be a permission rather than a convention. platform-api reads it because the console's
/// cross-tenant pages ask exactly the questions these views answer, and routing them through
/// <c>obs</c> means the service holding the platform's delete buttons needs no grant on any
/// base table — it cannot reach a password hash, a token, or one tenant's content.</para>
/// </summary>
public static class ObservabilityViewConfigurator
{
    /// <summary>Grafana's read-only reporting role.</summary>
    public const string RoleName = "dcms_grafana";

    /// <summary>platform-api's read-only reporting role.</summary>
    public const string PlatformRoleName = "dcms_platform";

    public const string SchemaName = "obs";

    /// <summary>Every role that may read the reporting views. Both are read-only.</summary>
    private static readonly string[] ReaderRoles = [RoleName, PlatformRoleName];

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

        var granted = new List<string>(ReaderRoles.Length);
        foreach (var role in ReaderRoles)
        {
            if (!await RoleExistsAsync(context, role, ct))
            {
                logger.LogWarning(
                    "Postgres role {Role} does not exist, so the {Schema} views were not granted to it. "
                    + "The client that connects as this role will fail until it is created — see "
                    + "infra/postgres/init/ (03-observability-role.sh for dcms_grafana, "
                    + "04-platform-role.sh for dcms_platform), which postgres-bootstrap re-runs on deploy.",
                    role,
                    SchemaName);
                continue;
            }

            // EF1002 fires because `role` is a variable rather than a compile-time constant. It
            // is still one of the two consts above and never reaches here from user input, and a
            // Postgres identifier cannot be bound as a parameter in any case — GRANT takes a
            // role name, not a value. Suppressed at the statement rather than the file so a
            // future interpolation of something genuinely user-supplied is still caught.
#pragma warning disable EF1002 // Risk of vulnerability to SQL injection.
            await context.Database.ExecuteSqlRawAsync(
                $"""
                 GRANT USAGE ON SCHEMA {SchemaName} TO {role};
                 GRANT SELECT ON ALL TABLES IN SCHEMA {SchemaName} TO {role};
                 ALTER DEFAULT PRIVILEGES IN SCHEMA {SchemaName} GRANT SELECT ON TABLES TO {role};
                 """,
                ct);
#pragma warning restore EF1002
            granted.Add(role);
        }

        logger.LogInformation(
            "Observability views applied (version {Version}) and granted to {Roles}.",
            ObservabilityViews.Version,
            granted.Count == 0 ? "nobody" : string.Join(", ", granted));
    }

    private static async Task<bool> RoleExistsAsync(DbContext context, string role, CancellationToken ct)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT 1 FROM pg_roles WHERE rolname = '{role}'";

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
