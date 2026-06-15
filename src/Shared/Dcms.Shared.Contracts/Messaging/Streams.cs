namespace Dcms.Shared.Contracts.Messaging;

/// <summary>
/// NATS JetStream stream names and their subject filters. Mirrored by
/// infra/nats/provision-streams.sh — keep both in sync.
/// </summary>
public static class Streams
{
    public const string Tenancy = "TENANCY";
    public const string Cms = "CMS";
    public const string Media = "MEDIA";
    public const string MediaEvents = "MEDIA_EVENTS";
    public const string Sites = "SITES";
    public const string SitesEvents = "SITES_EVENTS";
    public const string Analytics = "ANALYTICS";
    public const string Chat = "CHAT";
}

public static class Subjects
{
    // TENANCY
    public const string TenantCreated = "tenant.created";
    public const string TenantDomainVerified = "tenant.domain.verified";
    public const string PluginInstanceChanged = "plugin.instance.changed";
    public const string MembershipChanged = "membership.changed";

    // CMS
    public const string ContentPublished = "content.published";
    public const string ContentUnpublished = "content.unpublished";

    // MEDIA (work queue)
    public const string MediaProcessImage = "media.process.image";
    public const string MediaProcessVideo = "media.process.video";
    public const string MediaProcessAudio = "media.process.audio";

    // MEDIA_EVENTS
    public const string MediaProcessed = "media.processed";
    public const string MediaFailed = "media.failed";

    // SITES (work queue)
    public const string SitePublishRequested = "site.publish.requested";

    // SITES_EVENTS
    public const string SitePublished = "site.published";
    public const string SiteBuildFailed = "site.build.failed";

    // ANALYTICS
    public const string AnalyticsEvents = "analytics.events";

    // CHAT
    public const string ChatMessagePosted = "chat.message.posted";
}
