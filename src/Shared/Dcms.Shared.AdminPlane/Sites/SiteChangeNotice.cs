using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Messaging;

namespace Dcms.AdminApi.Sites;

/// <summary>
/// Tells every process that caches a site's route (site-host, content-api) that what the site
/// serves changed without a build: its search settings (#648), a rollback, a deployment.
/// </summary>
public static class SiteChangeNotice
{
    /// <summary>
    /// After the change is saved. A broker outage does not undo it: each route cache drops the
    /// entry within its five minutes regardless, so it is logged rather than failing the request.
    /// </summary>
    public static async Task PublishAsync(IEventPublisher events, Site site, ILogger logger, CancellationToken ct)
    {
        try
        {
            await events.PublishAsync(Subjects.SiteSettingsChanged,
                new SiteSettingsChanged(Guid.NewGuid(), DateTimeOffset.UtcNow, site.TenantId, site.Id, site.EnvironmentId), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Site {SiteId} changed, but its route caches were not told; they catch up within five minutes.", site.Id);
        }
    }
}
