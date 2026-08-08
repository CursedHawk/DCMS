using Dcms.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dcms.Identity.Forgejo;

/// <summary>
/// Drains the Forgejo user-sync outbox: retries the account/credential sync that the
/// inline path couldn't complete (e.g. Forgejo was down), with exponential backoff,
/// until it converges. Mirrors the polling shape of admin-api's OutboxDispatcher and
/// is resilient to Forgejo/Vault/DB being unavailable.
/// </summary>
public sealed class ForgejoSyncWorker(
    IServiceProvider services,
    IOptions<ForgejoOptions> options,
    ILogger<ForgejoSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private const int MaxAttempts = 12;

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

        var now = DateTimeOffset.UtcNow;
        var due = await db.ForgejoSyncOutbox
            .Where(o => o.NextAttemptAt <= now)
            .OrderBy(o => o.NextAttemptAt)
            .Take(50)
            .ToListAsync(ct);

        foreach (var row in due)
        {
            try
            {
                await sync.ApplyOutboxAsync(row, ct);
                db.ForgejoSyncOutbox.Remove(row);
                logger.LogInformation("Forgejo sync converged for user {UserId}.", row.UserId);
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
    }

    // 30s, 1m, 2m, 4m … capped at 1h.
    private static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Min(attempts, 12))));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
