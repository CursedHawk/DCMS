namespace Dcms.Shared.Contracts.Events;

/// <param name="AnalyticsEnabled">
/// Whether the tenant records analytics, so the assembler knows whether the page
/// has anything to ask consent for — a site that stores nothing must not greet
/// visitors with a cookie banner.
///
/// It travels on the message because the *producer* is the only party that can
/// answer it: site-builder connects as a least-privilege role with access to the
/// `sites` schema alone, so its own attempt to read `plugins.plugin_instances`
/// failed on every publish (42501) and silently fell back to "enabled".
///
/// Nullable, and defaulted, so a message already sitting in the stream from before
/// this field existed still deserializes. `null` means "not stated", which the
/// consumer reads as enabled — erring towards asking rather than tracking, the
/// same way the old fallback did.
/// </param>
public sealed record SitePublishRequested(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid SiteId,
    Guid BuildId,
    string RenderMode,
    bool? AnalyticsEnabled = null) : IDcmsEvent
{
    public int Version => 1;
}

public sealed record SitePublished(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid SiteId,
    Guid BuildId,
    string ArtifactPrefix) : IDcmsEvent
{
    public int Version => 1;
}

public sealed record SiteBuildFailed(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid SiteId,
    Guid BuildId,
    string Reason) : IDcmsEvent
{
    public int Version => 1;
}
