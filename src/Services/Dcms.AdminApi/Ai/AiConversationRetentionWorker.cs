using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Propagation;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Ai;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Ai;

/// <summary>
/// Ages out stored agent conversations.
///
/// <para><b>Why this table needs it more than most.</b> A conversation is not a chat log: every
/// tool result is stored whole, so a transcript carries draft content, analytics figures and
/// whatever else the agent read, and the IDE surface produces one of these per task rather than
/// one per sitting. Left alone, <c>ai.messages</c> becomes the largest table in the schema and
/// the most sensitive one, made mostly of copies of data that is already stored properly
/// elsewhere.</para>
///
/// <para><b>Two windows, because the two surfaces are not the same kind of record.</b> An IDE
/// run is a working note about a change that has since been committed to git — where the real
/// record of it lives — so it ages out quickly. A console conversation is often the only place
/// the reasoning behind a content change exists, so it keeps the longer window. Both are
/// configurable, and both count from the last activity rather than from creation, so a
/// conversation somebody is still returning to is never taken out from under them.</para>
///
/// <para><b>What is never swept: anything shared, and anything archived.</b> Sharing to the
/// workspace is a deliberate act that hands a transcript to colleagues, and archiving is what
/// this product already offers for "keep this". Deleting either on a timer would break the one
/// promise the feature makes.</para>
/// </summary>
public sealed class AiConversationRetentionWorker(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<AiConversationRetentionWorker> logger) : BackgroundService
{
    private readonly int _consoleDays = configuration.GetValue("Ai:ConversationRetentionDays", 180);

    private readonly int _ideDays = configuration.GetValue("Ai:IdeRunRetentionDays", 45);

    private readonly TimeSpan _interval =
        TimeSpan.FromHours(configuration.GetValue("Ai:RetentionSweepHours", 24));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Nothing here is urgent, and a sweep on the startup path would compete with migrations.
        await Task.Delay(TimeSpan.FromMinutes(7), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AI conversation retention sweep failed; retrying next cycle.");
            }
            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var leadership = await PostgresAdvisoryLock.TryAcquireAsync(
            connectionString, PostgresAdvisoryLock.AiConversationRetentionLockKey, logger, ct);

        if (leadership is null)
        {
            logger.LogDebug("Another replica is running AI conversation retention; skipping this pass.");
            return;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();
        var auditScope = scope.ServiceProvider.GetRequiredService<AuditScope>();
        var ambient = scope.ServiceProvider.GetRequiredService<AuditAmbient>();
        using var ambientScope = ambient.Enter(auditScope);

        // One record for the pass rather than one per deleted row. The rows are gone before
        // anything could read a field diff off them, which is the same trade the notification
        // and analytics sweeps make.
        using var bulkSuppressed = auditScope.SuppressBulkCapture();

        var now = DateTimeOffset.UtcNow;
        var consoleCutoff = now - TimeSpan.FromDays(_consoleDays);
        var ideCutoff = now - TimeSpan.FromDays(_ideDays);

        // Messages and runs go with their conversation on the database's cascade, so this is
        // one statement rather than three — and cannot leave an orphan behind if it is
        // interrupted halfway.
        //
        // IgnoreQueryFilters because retention is cross-tenant by nature and this worker has no
        // ambient tenant: the filter would match nothing at all.
        var deleted = await db.Conversations.IgnoreQueryFilters()
            .Where(c => c.ArchivedAt == null
                        && c.Visibility == AiConversationVisibility.Private
                        && ((c.Surface == AiSurfaces.Ide && c.UpdatedAt < ideCutoff)
                            || (c.Surface != AiSurfaces.Ide && c.UpdatedAt < consoleCutoff)))
            .ExecuteDeleteAsync(ct);

        if (deleted == 0) return;

        recorder.Record(AuditActions.AiConversationsPruned)
            .Platform()
            .As(AuditCategory.TenantState)
            .For("ai", "conversations", "private conversations untouched beyond their retention window")
            .With("consoleCutoff", consoleCutoff.ToString("O"))
            .With("ideCutoff", ideCutoff.ToString("O"))
            .With("consoleRetentionDays", _consoleDays)
            .With("ideRetentionDays", _ideDays)
            .With("conversationsDeleted", deleted);

        await recorder.FlushAsync(ct);

        logger.LogInformation("AI conversation retention removed {Count} conversation(s).", deleted);
    }
}
