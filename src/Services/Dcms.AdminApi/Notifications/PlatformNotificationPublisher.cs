using System.Text.Json;
using Dcms.Shared.Data.Notifications;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dcms.AdminApi.Notifications;

/// <param name="DedupeKey">
/// Names the fact, not the pass that noticed it. Every raiser here re-reads a window of history
/// on each cycle, so this is what turns "tell the operators about the last day's certificate
/// attempts" into an operation that can run every two minutes forever.
/// </param>
public sealed record PlatformNotificationRequest(
    string Kind,
    NotificationSeverity Severity,
    string DedupeKey,
    object? Params = null,
    string? LinkPath = null,
    string? ResourceType = null,
    Guid? ResourceId = null);

public interface IPlatformNotificationPublisher
{
    /// <summary>
    /// Records a notification for the platform's operators. True when it was new, false when it
    /// had already been raised or could not be written.
    ///
    /// <para>Never throws for an ordinary failure: a notification is a courtesy on top of
    /// something that has already happened, and losing the courtesy must not take the caller's
    /// real work with it.</para>
    /// </summary>
    Task<bool> RaiseAsync(PlatformNotificationRequest request, CancellationToken ct = default);
}

/// <summary>
/// Writes platform notifications.
///
/// <para><b>No recipient fan-out</b>, unlike <see cref="NotificationPublisher"/>. The audience is
/// every holder of the SuperAdmin global role, which lives in identity — a service admin-api does
/// not read users from, and should not start reading users from in order to draw a badge. So the
/// audience is implicit and read state is created when an operator acts. See
/// <see cref="PlatformNotificationRead"/> for what that costs.</para>
/// </summary>
public sealed class PlatformNotificationPublisher(
    NotificationsDbContext db,
    ILogger<PlatformNotificationPublisher> logger) : IPlatformNotificationPublisher
{
    public async Task<bool> RaiseAsync(PlatformNotificationRequest request, CancellationToken ct = default)
    {
        try
        {
            db.PlatformNotifications.Add(new PlatformNotification
            {
                Kind = request.Kind,
                Severity = request.Severity,
                ParamsJson = request.Params is null ? "{}" : JsonSerializer.Serialize(request.Params),
                LinkPath = request.LinkPath,
                ResourceType = request.ResourceType,
                ResourceId = request.ResourceId,
                DedupeKey = request.DedupeKey,
            });

            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Raised platform notification {Kind} ({DedupeKey}).", request.Kind, request.DedupeKey);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Already told them. The expected outcome on most passes, so it is not a warning.
            db.ChangeTracker.Clear();
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Raising platform notification {Kind} failed.", request.Kind);
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
