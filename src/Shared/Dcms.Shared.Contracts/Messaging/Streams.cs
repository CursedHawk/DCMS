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
    public const string Email = "EMAIL";
    public const string Audit = "AUDIT";
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

    // EMAIL (work queue)
    public const string EmailSend = "email.send";

    // AUDIT
    //
    // Two subjects, in opposite directions, and keeping them apart matters: a writer that
    // consumed its own fan-out would chain every record twice.

    /// <summary>
    /// Inbound. Records from the two services that cannot reach the <c>audit</c> schema —
    /// email-worker, which has no database, and site-builder, which is confined to
    /// <c>sites</c>. admin-api's ingest consumer drains this into the outbox, so everything
    /// still reaches the chain by one path.
    /// </summary>
    public const string AuditSubmitted = "audit.submitted";

    /// <summary>
    /// Outbound fan-out, published after a record is chained. For sinks outside the platform —
    /// a SIEM, a webhook — never for getting a record <i>into</i> the log. The system of
    /// record is the audit schema, not this stream.
    /// </summary>
    public const string AuditRecorded = "audit.recorded";
}
