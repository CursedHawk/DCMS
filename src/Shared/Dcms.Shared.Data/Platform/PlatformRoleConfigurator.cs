using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Data.Platform;

/// <summary>
/// Sets the <c>dcms_platform</c> login password from Vault, on every migration run.
///
/// <para><b>Why this is here and not in the init script.</b> The role is created by
/// <c>infra/postgres/init/04-platform-role.sh</c>, which runs inside a bare <c>postgres</c>
/// image — no Vault client, and no way to get one; the image has neither curl nor wget. Any
/// password that script could set would have to arrive through the environment, which is the
/// place this platform is deliberately moving secrets OUT of. So it creates the role with no
/// password at all, and this closes the loop from a process that can read Vault.</para>
///
/// <para><b>Why admin-api and not platform-api.</b> A role cannot set its own password before
/// it can log in, and platform-api's whole connection is that login. admin-api connects as the
/// schema owner, so it already holds every privilege <c>dcms_platform</c> has and more —
/// giving it this one password is not an escalation, it is a strictly smaller credential than
/// the one it already uses. It runs here, in the migration job, because that is the one
/// process on this platform allowed to run DDL.</para>
///
/// <para><b>Two copies of one secret, and why.</b> The Vault config provider reads
/// <c>secret/dcms/&lt;own service&gt;</c> and nothing else, so admin-api cannot read
/// platform-api's path and vice versa. The value therefore lives in both, and
/// <c>infra/vault/apply.sh --seed</c> generates it once and writes both halves so they cannot
/// disagree at creation. A later mismatch is loud rather than subtle: platform-api fails to
/// authenticate to Postgres at startup.</para>
/// </summary>
public static class PlatformRoleConfigurator
{
    public const string RoleName = "dcms_platform";

    /// <summary>Config key the Vault provider maps <c>Platform__DbPassword</c> onto.</summary>
    public const string PasswordConfigKey = "Platform:DbPassword";

    public static async Task ApplyAsync(
        DbContext context, string? password, ILogger logger, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            // Not fatal, and deliberately so. A cluster that has not been seeded yet should
            // still migrate: the console is one service, and refusing to run every other
            // schema change because of it would turn a missing secret into a stuck platform.
            // platform-api will fail to connect and say why, which is where this belongs.
            logger.LogWarning(
                "{Key} is not configured, so the {Role} login password was not set. platform-api "
                + "will fail to authenticate until `infra/vault/apply.sh --seed` has run.",
                PasswordConfigKey,
                RoleName);
            return;
        }

        if (!await RoleExistsAsync(context, ct))
        {
            logger.LogWarning(
                "Postgres role {Role} does not exist, so its password was not set. It is created by "
                + "infra/postgres/init/04-platform-role.sh, which postgres-bootstrap re-runs on deploy.",
                RoleName);
            return;
        }

        // ALTER ROLE is a utility statement, and Postgres accepts no bind parameters in one:
        // `PREPARE t AS ALTER ROLE ... PASSWORD $1` is a syntax error. The obvious workaround —
        // interpolating the password into the statement text — is the one thing worth avoiding
        // here, because statement text is exactly what lands in pg_stat_activity and in a query
        // log, and a credential that reaches a log is a credential.
        //
        // So the password travels as a real bind parameter into set_config, and format('%L')
        // does the quoting inside a DO block, where dynamic SQL is allowed. Verified against a
        // password containing a single quote, which is the case naive escaping gets wrong.
        // The GUC is cleared in the same command rather than left for the session to carry.
        await context.Database.ExecuteSqlRawAsync(
            // $$ raw string: with two dollars an interpolation hole is {{ }}, so {0} survives
            // as the EF parameter placeholder it needs to be. With a single $ it would have
            // been read as an interpolation of the literal 0 — which compiles, and silently
            // sends `set_config('...', 0, false)` with the password bound to nothing.
            $$"""
              SELECT set_config('dcms.platform_role_password', {0}, false);
              DO $do$ BEGIN
                EXECUTE format(
                  'ALTER ROLE {{RoleName}} WITH LOGIN PASSWORD %L',
                  current_setting('dcms.platform_role_password'));
              END $do$;
              SELECT set_config('dcms.platform_role_password', '', false);
              """,
            [password],
            ct);

        logger.LogInformation("Login password for {Role} converged from configuration.", RoleName);
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
}
