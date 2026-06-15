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

public sealed record AnalyticsEvent(
    DateTimeOffset OccurredAt,
    string Type,
    string Path,
    string? Referrer,
    string? SessionId,
    string? VisitorHash,
    JsonElement? Props);
