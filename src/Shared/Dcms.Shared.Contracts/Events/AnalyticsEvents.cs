using System.Text.Json;

namespace Dcms.Shared.Contracts.Events;

public sealed record AnalyticsEventBatch(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    IReadOnlyList<AnalyticsEvent> Events) : ITenantEvent
{
    public int Version => 1;
}

/// <param name="Device">"desktop", "mobile", "tablet" or "bot" — derived at ingest.</param>
/// <param name="Country">
/// ISO 3166-1 alpha-2, resolved at ingest from the request. Never taken from the
/// beacon payload: a page can claim any country it likes.
/// </param>
/// <param name="SiteId">
/// The site the hit came from, as site-host resolved it from the Host (X-Dcms-Site). Null for an
/// externally hosted site, which reaches content-api without passing through site-host.
/// </param>
/// <param name="Hostname">The domain the visitor used (X-Dcms-Site-Host); a site can have several.</param>
public sealed record AnalyticsEvent(
    DateTimeOffset OccurredAt,
    string Type,
    string Path,
    string? Referrer,
    string? SessionId,
    string? VisitorHash,
    JsonElement? Props,
    string? Device = null,
    string? Browser = null,
    string? Os = null,
    string? Country = null,
    string? UtmSource = null,
    string? UtmMedium = null,
    string? UtmCampaign = null,
    Guid? SiteId = null,
    string? Hostname = null);
