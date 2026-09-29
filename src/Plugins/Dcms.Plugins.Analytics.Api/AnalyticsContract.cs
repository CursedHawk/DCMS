using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.Analytics.Api;

public static class AnalyticsPermissions
{
    /// <summary>The platform permission the dashboard uses.</summary>
    public const string Read = "analytics:read";
}

/// <param name="Type">Event name, e.g. <c>signup</c> or <c>download</c>; <c>pageview</c> is the site beacon's.</param>
/// <param name="Path">The page it concerns; "/" when none.</param>
/// <param name="SessionId">An opaque per-visit id, hashed into an anonymous visitor.</param>
/// <param name="Props">Free-form properties, stored as given.</param>
public sealed record TrackEvent(string Type, string Path = "/", string? SessionId = null, JsonElement? Props = null);

/// <param name="Days">The window, ending today; 1 to 365.</param>
public sealed record AnalyticsSummaryRequest(int Days = 30);

public sealed record PageCount(string Path, long Count);

public sealed record AnalyticsSummary(
    DateTimeOffset From, DateTimeOffset To, long Events, long Pageviews, long Visitors, long Sessions, IReadOnlyList<PageCount> TopPages);

/// <summary>
/// The tenant's site analytics. Other plugins record their own events into the same store (a
/// download, a signup), so they appear on the dashboard beside page views.
/// </summary>
[DcmsContract("analytics.tracking", 1, Description = "Record events into, and summarise, the tenant's site analytics.")]
public interface IAnalytics
{
    /// <summary>Queued, not written inline; a preview (sandbox) request records nothing.</summary>
    [Operation(OpRisk.Safe, Description = "Record one analytics event.")]
    Task TrackAsync(TrackEvent input, CancellationToken ct);

    [Operation(OpRisk.Read, Permission = AnalyticsPermissions.Read, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Totals and top pages for the last N days.")]
    Task<AnalyticsSummary> SummaryAsync(AnalyticsSummaryRequest input, CancellationToken ct);
}
