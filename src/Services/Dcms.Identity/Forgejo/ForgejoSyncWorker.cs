using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Propagation;
using Dcms.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dcms.Identity.Forgejo;

/// <summary>
/// Drains the Forgejo user-sync outbox: retries the account/credential sync that the
/// inline path couldn't complete (e.g. Forgejo was down), with exponential backoff,
/// until it converges. Mirrors the polling shape of admin-api's OutboxDispatcher and
/// is resilient to Forgejo/Vault/DB being unavailable.
///
/// <para>Rows are claimed with <c>FOR UPDATE SKIP LOCKED</c> so sibling replicas take disjoint
/// sets. Without it every replica drains the same rows: the same Forgejo account and credential
/// calls are made N times, and the replicas then race on <c>Remove(row)</c> — one deletes it and
/// the others' SaveChanges affects zero rows.</para>
/// </summary>
public sealed class ForgejoSyncWorker(
    IServiceProvider services,
    IOptions<ForgejoOptions> options,
    ILogger<ForgejoSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private const int MaxAttempts = 12;

    /// <summary>
    /// Claims a batch for this replica. Columns are EF's default PascalCase identifiers (only the
    /// table name is snake_cased), so they must be double-quoted in raw SQL.
    /// </summary>
    private const string ClaimSql =
        """
        SELECT * FROM identity.forgejo_sync_outbox
        WHERE "NextAttemptAt" <= now()
        ORDER BY "NextAttemptAt"
        FOR UPDATE SKIP LOCKED
        LIMIT 50
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return; // provisioning not configured; nothing to do

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Forgejo sync worker pass failed; retrying.");
            }
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var sync = scope.ServiceProvider.GetRequiredService<ForgejoUserSync>();
        var ambient = scope.ServiceProvider.GetRequiredService<AuditAmbient>();
        var auditScope = scope.ServiceProvider.GetRequiredService<AuditScope>();

        var now = DateTimeOffset.UtcNow;

        // One transaction around the whole pass: the row locks must outlive the Forgejo calls,
        // or a sibling could claim a row this replica has read but not yet removed. The batch is
        // capped at 50 and each row is a couple of HTTP calls, so the lock is held for seconds --
        // and every other replica SKIP LOCKEDs straight past it rather than waiting.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var due = await db.ForgejoSyncOutbox.FromSqlRaw(ClaimSql).ToListAsync(ct);

        foreach (var row in due)
        {
            // The person whose credential change this is. Restored per row: a batch may hold
            // several people's, and attributing the second to the first would be worse than
            // attributing it to nobody.
            AuditPropagation.Restore(auditScope, AuditPropagation.FromJson(row.ContextJson));
            using var _ = ambient.Enter(auditScope);

            try
            {
                await sync.ApplyOutboxAsync(row, ct);
                db.ForgejoSyncOutbox.Remove(row);
                logger.LogInformation("Forgejo sync converged for user {UserId}.", row.UserId);
            }
            catch (ForgejoAdoptionRefusedException ex)
            {
                // Permanent. Backing off twelve times before dropping it would only delay the
                // same answer and bury the reason under retry noise.
                db.ForgejoSyncOutbox.Remove(row);
                logger.LogError(ex,
                    "Forgejo sync refused for user {UserId}; dropping row.", row.UserId);
            }
            catch (Exception ex)
            {
                row.Attempts++;
                if (row.Attempts >= MaxAttempts)
                {
                    // Dead-letter: give up (e.g. an undecryptable password after a key
                    // rotation). The user self-heals on their next successful login.
                    db.ForgejoSyncOutbox.Remove(row);
                    logger.LogError(ex,
                        "Forgejo sync gave up after {Attempts} attempts for user {UserId}; dropping row (will self-heal on next login).",
                        row.Attempts, row.UserId);
                }
                else
                {
                    row.NextAttemptAt = now + Backoff(row.Attempts);
                    row.LastError = Truncate(ex.Message, 500);
                    logger.LogWarning(ex,
                        "Forgejo sync retry {Attempt} failed for user {UserId}; next at {Next:o}.",
                        row.Attempts, row.UserId, row.NextAttemptAt);
                }
            }
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    // 30s, 1m, 2m, 4m … capped at 1h.
    private static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Min(attempts, 12))));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
