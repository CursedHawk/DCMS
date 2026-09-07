using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dcms.Shared.Data;

/// <summary>
/// A session-scoped Postgres advisory lock, used to serialise schema changes across instances.
///
/// <para>Migrations are meant to run as a one-shot job before anything is rolled. This is what
/// stops the other case from corrupting anything: two service instances starting at the same
/// moment, each calling <c>MigrateAsync</c>, racing on <c>CREATE TABLE</c> and on inserts into
/// the same <c>__ef_migrations_history</c>.</para>
///
/// <para>The lock is held by the <b>session</b>, not the transaction, so this opens a dedicated
/// connection and keeps it open for the duration. Taking the lock on a pooled EF connection would
/// release it the moment that connection went back to the pool — which is to say, silently, and
/// usually in the middle of the work it was protecting.</para>
/// </summary>
public static class PostgresAdvisoryLock
{
    /// <summary>Schema migrations for the platform database (admin-api owns these).</summary>
    public const long MigrationLockKey = 0x44434D5300000001;

    /// <summary>Identity's own schema and seed data.</summary>
    public const long IdentityLockKey = 0x44434D5300000002;

    /// <summary>Hourly audit housekeeping: partitions, sealing, retention.</summary>
    public const long AuditMaintenanceLockKey = 0x44434D5300000003;

    /// <summary>Six-hourly analytics event pruning.</summary>
    public const long AnalyticsRetentionLockKey = 0x44434D5300000004;

    /// <summary>Elects the single replica that syncs Meta (Facebook/Instagram) feeds per pass.</summary>
    public const long MetaSyncLockKey = 0x44434D5300000005;

    /// <summary>Elects the single replica that refreshes Meta long-lived access tokens.</summary>
    public const long MetaTokenRefreshLockKey = 0x44434D5300000006;

    /// <summary>Notification retention. One replica per sweep; the others skip the pass.</summary>
    public const long NotificationRetentionLockKey = 0x44434D5300000007;

    /// <summary>
    /// Elects the single edge replica that renews TLS certificates per sweep. Two replicas
    /// renewing the same hostname would double the orders counted against the CA's rate limit
    /// and leave the loser's row overwriting the winner's.
    /// </summary>
    public const long EdgeCertificateRenewalLockKey = 0x44434D5300000008;

    /// <summary>
    /// Elects the replica that turns platform facts into operator notifications. Correctness
    /// does not depend on it — the dedupe key does — but without it every replica does the same
    /// work in order to find out that another one already did it.
    /// </summary>
    public const long PlatformNotificationSweepLockKey = 0x44434D5300000009;

    /// <summary>
    /// Blocks until the lock is held. Deliberately blocking rather than
    /// <c>pg_try_advisory_lock</c>: a second instance arriving mid-migration should wait and then
    /// find there is nothing left to do. Giving up and carrying on would let it serve traffic
    /// against a half-migrated schema, which is the failure this exists to prevent.
    /// </summary>
    public static async Task<IAsyncDisposable> AcquireAsync(
        string connectionString,
        long key,
        ILogger logger,
        CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);

            logger.LogInformation("Waiting for advisory lock {Key:X}.", key);
            await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection))
            {
                acquire.Parameters.AddWithValue("key", key);
                await acquire.ExecuteNonQueryAsync(ct);
            }
            logger.LogInformation("Advisory lock {Key:X} held.", key);

            return new Handle(connection, key, logger);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Takes the lock if it is free, and returns null if another instance holds it.
    ///
    /// <para>This is the leader election for periodic singleton jobs. Their work is idempotent,
    /// so N replicas running it concurrently corrupts nothing — but it is not free: the analytics
    /// pruner issues twenty ten-thousand-row DELETEs per pass, and running that N times over
    /// generates N times the WAL on the disk the job exists to protect.</para>
    ///
    /// <para>Deliberately per-pass rather than a long-held leadership lease. A replica that dies
    /// mid-pass releases the lock when its session closes, and the next replica to wake simply
    /// picks the work up -- no lease timeout to tune, and no window where a job is nobody's.</para>
    /// </summary>
    public static async Task<IAsyncDisposable?> TryAcquireAsync(
        string connectionString,
        long key,
        ILogger logger,
        CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);

            await using var attempt = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            attempt.Parameters.AddWithValue("key", key);
            var acquired = await attempt.ExecuteScalarAsync(ct) as bool? ?? false;

            if (!acquired)
            {
                await connection.DisposeAsync();
                return null;
            }

            return new Handle(connection, key, logger);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Handle(NpgsqlConnection connection, long key, ILogger logger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                // Best effort. Closing the session releases the lock regardless, so a failure
                // here must not mask whatever the caller was doing when it threw.
                await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                release.Parameters.AddWithValue("key", key);
                await release.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to release advisory lock {Key:X}; closing the session will.", key);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
