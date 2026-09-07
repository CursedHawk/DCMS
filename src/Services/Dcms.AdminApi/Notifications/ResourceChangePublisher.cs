using Microsoft.AspNetCore.SignalR;

namespace Dcms.AdminApi.Notifications;

/// <summary>
/// Tells every console open on a tenant that a class of data has changed, so it can refetch.
/// See <see cref="ResourceTags"/> for what a tag means and why this is not a notification.
/// </summary>
public interface IResourceChangePublisher
{
    /// <param name="tenantId">The tenant whose consoles should refetch.</param>
    /// <param name="tag">One of <see cref="ResourceTags"/>.</param>
    /// <param name="id">
    /// The row that changed, where the producer knows it. Optional, and consoles are free to
    /// ignore it: a list view refetches the list either way, and only a detail view open on
    /// precisely that row can do anything cheaper with it.
    /// </param>
    Task PublishAsync(Guid tenantId, string tag, string? id = null, CancellationToken ct = default);
}

public sealed class ResourceChangePublisher(
    IHubContext<NotificationHub> hub,
    ILogger<ResourceChangePublisher> logger) : IResourceChangePublisher
{
    public async Task PublishAsync(
        Guid tenantId, string tag, string? id = null, CancellationToken ct = default)
    {
        try
        {
            await hub.Clients
                .Group(NotificationHub.TenantGroup(tenantId))
                .SendAsync("ResourceChanged", new { tag, id }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missed refetch hint costs a stale table until the next navigation or the
            // fallback poll. The write it followed has already happened and must not be
            // undone by a socket problem.
            logger.LogDebug(ex, "Publishing resource change {Tag} for tenant {Tenant} failed.",
                tag, tenantId);
        }
    }
}
