using System.Text.Json;

namespace Dcms.Shared.Contracts.Events;

public sealed record AnalyticsEventBatch(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    IReadOnlyList<AnalyticsEvent> Events) : IDcmsEvent
{
    public int Version => 1;
}

/// <param name="Device">"desktop", "mobile", "tablet" or "bot" — derived at ingest.</param>
/// <param name="Country">
/// ISO 3166-1 alpha-2, resolved at ingest from the request. Never taken from the
/// beacon payload: a page can claim any country it likes.
/// </param>
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
    string? UtmCampaign = null);
