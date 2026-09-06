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

    /// <summary>
    /// Inbound notification requests from services that cannot reach the notifications
    /// schema. Same shape as AUDIT's inbound half: admin-api owns the tables, everyone
    /// else asks it to write.
    /// </summary>
    public const string Notify = "NOTIFY";
}

public static class Subjects
{
    // TENANCY
    public const string TenantCreated = "tenant.created";
    public const string TenantDomainVerified = "tenant.domain.verified";

    /// <summary>
    /// Tenant suspended or resumed. Consumed by site-host, which caches domain -> tenant
    /// resolution: without an invalidation signal a suspension would appear to work in the
    /// console and keep serving the public site until a cache entry happened to expire, which
    /// is the worst kind of half-working.
    /// </summary>
    public const string TenantSuspended = "tenant.suspended";
    public const string TenantResumed = "tenant.resumed";
    public const string PluginInstanceChanged = "plugin.instance.changed";
    public const string MembershipChanged = "membership.changed";

    /// <summary>
    /// An operator asked for a managed certificate to be reissued. On the TENANCY stream, whose
    /// subject list carries `edge.>` for it -- that stream is already the platform's control
    /// plane rather than strictly tenancy (it also carries plugin.instance.> and membership.>),
    /// and a stream of its own would be more infrastructure than one rare message deserves.
    /// </summary>
    public const string ManagedCertificateReissueRequested = "edge.certificate.reissue-requested";

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
    //
    // One subject per render mode, because the three cost wildly different amounts and they
    // used to share a queue. Measured on a 4-core host: a Mode C publish is an unzip and
    // takes ~340 ms, a Mode A publish assembles committed HTML/CSS in-process and takes
    // ~1 s, and a Mode B publish runs a package install plus a vite build inside a sandbox
    // container and takes ~30 s. On one consumer with in-order delivery, that made the Mode C
    // publish's p95 **34.8 seconds** whenever a Mode B build was running -- 95x -- while its
    // minimum stayed at 338 ms. Head-of-line blocking, not saturation: it was not slow, it
    // was waiting.
    //
    // Splitting the SUBJECT rather than the concurrency is what fixes it. Raising the
    // consumer's concurrency does not: a Mode B sandbox is allotted DCMS_BUILD_CPUS (2) of
    // the host's four, so two of them at once are the whole machine, and nothing would stop
    // both slots holding a Mode B build. Separate subjects let separate consumers drain at
    // their own rates, so an expensive build can never be in front of a cheap one.
    //
    // Which subject goes in which lane is site-builder's configuration (SiteBuildLane), not
    // a property of the wire -- so moving Mode A between lanes costs one line there and
    // nothing here.

    /// <summary>Mode C (StaticFiles): extract an already-staged bundle. Sub-second.</summary>
    public const string SitePublishRequestedStaticFiles = "site.publish.requested.staticfiles";

    /// <summary>Mode A (StaticPrerender): assemble committed HTML/CSS in-process. About a second.</summary>
    public const string SitePublishRequestedStaticPrerender = "site.publish.requested.staticprerender";

    /// <summary>Mode B (ReactApp): install and build in a sandbox container. Tens of seconds.</summary>
    public const string SitePublishRequestedReactApp = "site.publish.requested.reactapp";

    /// <summary>
    /// The pre-split subject. <b>Nothing publishes here any more</b> — it exists so a message
    /// written by an admin-api from before the split still has a consumer during the rolling
    /// deploy that introduces it. The SITES stream is work-queue retention, so a message no
    /// consumer's filter matches is never delivered and never removed: the build would sit in
    /// Queued until the reaper failed it, and the author would be told their publish failed
    /// for no reason anyone could see.
    ///
    /// <para>Retire the <c>site-builder</c> durable a release after the split has shipped —
    /// there is a <c>retire_consumer</c> helper in infra/nats/provision-streams.sh, and the
    /// lane logs a warning whenever it actually drains something, so "is it still needed" is
    /// a question the logs answer.</para>
    /// </summary>
    public const string SitePublishRequestedLegacy = "site.publish.requested";

    /// <summary>
    /// The subject a publish of <paramref name="renderMode"/> belongs on.
    ///
    /// <para>Matched case-insensitively against the render mode as it travels on the message —
    /// a string, not the enum, because Contracts deliberately does not reference the data
    /// layer. An unrecognised mode routes to the prerender subject, which mirrors what the
    /// builder does with it: <c>BuildAsync</c> dispatches ReactApp and StaticFiles explicitly
    /// and treats everything else as Mode A. Routing an unknown mode to the cheap lane instead
    /// would put a build of unknown cost in front of the ones the lane exists to keep fast.</para>
    /// </summary>
    public static string SitePublishSubjectFor(string? renderMode) =>
        string.Equals(renderMode, "StaticFiles", StringComparison.OrdinalIgnoreCase)
            ? SitePublishRequestedStaticFiles
            : string.Equals(renderMode, "ReactApp", StringComparison.OrdinalIgnoreCase)
                ? SitePublishRequestedReactApp
                : SitePublishRequestedStaticPrerender;

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

    // NOTIFY
    //
    // One subject, inbound only. admin-api raises its own notifications in-process — it owns
    // the schema — so this exists for the services that do not: content-api (form
    // submissions) today, and any later producer without database access to the schema.
    //
    // There is deliberately no outbound "notification.raised" fan-out. Delivery to browsers
    // is the SignalR hub's job, and a second copy on the bus would be a second thing to keep
    // consistent with the table that is already the system of record.
    public const string NotifyRaise = "notify.raise";
}
