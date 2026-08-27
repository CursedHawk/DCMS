using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Dcms.Shared.Telemetry;

/// <summary>
/// What the platform does, as numbers. One meter, one class, registered as a singleton — the
/// seam a new subsystem adds a counter to rather than inventing its own meter that nobody
/// remembers to subscribe.
///
/// <para><b>Cardinality is the whole design constraint.</b> A Prometheus time series exists for
/// every distinct combination of label values, forever, in memory. So a label is allowed here
/// only when its value set is bounded by something small: a tenant count, a provider name, an
/// outcome. Never a user id, an email address, an IP, a raw URL path, a correlation id, or a
/// resource id — those are all unbounded, and one of them on a hot counter is how a metrics
/// store runs a host out of memory. The questions those would answer are answered from
/// Postgres (the <c>obs.*</c> views) or from Loki, both of which are built for high
/// cardinality and neither of which keeps it resident.</para>
///
/// <para>Emitted through <c>System.Diagnostics.Metrics</c> and exported by the OpenTelemetry
/// wiring in <c>AddDcmsServiceDefaults</c>, which subscribes <see cref="MeterName"/>
/// explicitly. That subscription is not optional and not automatic: an unsubscribed meter is
/// silently dropped by the SDK, which is exactly how the audit meter went a release without
/// exporting anything while the runbook said it did.</para>
/// </summary>
public sealed class DcmsMetrics : IDisposable
{
    public const string MeterName = "Dcms.Platform";

    private readonly Meter _meter;

    // Content
    private readonly Counter<long> _contentPublished;
    private readonly Counter<long> _contentUnpublished;

    // Media
    private readonly Counter<long> _mediaProcessed;
    private readonly Counter<long> _mediaBytes;
    private readonly Histogram<double> _mediaDuration;

    // Sites
    private readonly Counter<long> _siteBuilds;
    private readonly Histogram<double> _siteBuildDuration;

    // Email
    private readonly Counter<long> _emailSent;
    private readonly Counter<long> _emailFailed;

    // AI
    private readonly Counter<long> _aiTokens;
    private readonly Histogram<double> _aiDuration;

    // Engagement
    private readonly Counter<long> _chatMessages;
    private readonly Counter<long> _formSubmissions;
    private readonly Counter<long> _searchQueries;

    // Identity / growth
    private readonly Counter<long> _logins;
    private readonly Counter<long> _signups;
    private readonly Counter<long> _tenantsCreated;
    private readonly Counter<long> _pluginInstanceChanges;

    // Pipelines
    private readonly ConcurrentDictionary<string, long> _outboxDepths = new();
    private readonly ObservableGauge<long> _outboxDepth;
    private readonly Histogram<double> _outboxLag;
    private readonly Counter<long> _messagesConsumed;
    private readonly Histogram<double> _messageHandlingDuration;

    public DcmsMetrics(IMeterFactory factory)
    {
        _meter = factory.Create(MeterName);

        _contentPublished = _meter.CreateCounter<long>(
            "dcms.content.published", "{item}", "Content versions published.");
        _contentUnpublished = _meter.CreateCounter<long>(
            "dcms.content.unpublished", "{item}", "Content items withdrawn from delivery.");

        _mediaProcessed = _meter.CreateCounter<long>(
            "dcms.media.processed", "{asset}", "Media assets a worker finished with, by outcome.");
        _mediaBytes = _meter.CreateCounter<long>(
            "dcms.media.bytes", "By", "Bytes written by media processing, originals and variants.");
        _mediaDuration = _meter.CreateHistogram<double>(
            "dcms.media.process.duration", "s", "Wall time of one media processing job.");

        _siteBuilds = _meter.CreateCounter<long>(
            "dcms.site.build", "{build}", "Site builds completed, by mode and outcome.");
        _siteBuildDuration = _meter.CreateHistogram<double>(
            "dcms.site.build.duration", "s", "Wall time of one site build, sandbox included.");

        _emailSent = _meter.CreateCounter<long>(
            "dcms.email.sent", "{message}", "Messages the relay accepted.");
        _emailFailed = _meter.CreateCounter<long>(
            "dcms.email.failed", "{message}", "Messages the relay refused, by reason class.");

        _aiTokens = _meter.CreateCounter<long>(
            "dcms.ai.tokens", "{token}", "Model tokens, by direction. The cost driver.");
        _aiDuration = _meter.CreateHistogram<double>(
            "dcms.ai.request.duration", "s", "Round-trip time of one model call.");

        _chatMessages = _meter.CreateCounter<long>(
            "dcms.chat.message", "{message}", "Live-chat messages posted, by sender kind.");
        _formSubmissions = _meter.CreateCounter<long>(
            "dcms.form.submission", "{submission}", "Form submissions accepted.");
        _searchQueries = _meter.CreateCounter<long>(
            "dcms.search.query", "{query}", "Delivery search queries served.");

        _logins = _meter.CreateCounter<long>(
            "dcms.auth.login", "{attempt}", "Sign-in attempts, by method and outcome.");
        _signups = _meter.CreateCounter<long>(
            "dcms.auth.signup", "{user}", "Platform accounts created, by method.");
        _tenantsCreated = _meter.CreateCounter<long>(
            "dcms.tenant.created", "{tenant}", "Tenants provisioned.");
        _pluginInstanceChanges = _meter.CreateCounter<long>(
            "dcms.plugin.instance", "{change}", "Plugin instances created, enabled or disabled.");

        // A gauge rather than a counter: depth is a level, and the question asked of it is
        // always "is it going up", which a counter cannot answer. Observed from a dictionary
        // the dispatcher writes on every poll, because the value only exists after a query and
        // the meter must not be the thing that runs one.
        _outboxDepth = _meter.CreateObservableGauge(
            "dcms.outbox.depth", ObserveOutboxDepths, "{message}", "Unsent rows in a transactional outbox.");
        _outboxLag = _meter.CreateHistogram<double>(
            "dcms.outbox.lag", "s", "Age of a row when the dispatcher picked it up.");
        _messagesConsumed = _meter.CreateCounter<long>(
            "dcms.messages.consumed", "{message}", "JetStream messages handled, by stream and outcome.");
        _messageHandlingDuration = _meter.CreateHistogram<double>(
            "dcms.messages.handling.duration", "s", "Time spent in one message handler.");
    }

    public void ContentPublished(Guid tenantId, string contentType) =>
        _contentPublished.Add(1, Tenant(tenantId), new("content_type", contentType));

    public void ContentUnpublished(Guid tenantId, string contentType) =>
        _contentUnpublished.Add(1, Tenant(tenantId), new("content_type", contentType));

    public void MediaProcessed(Guid tenantId, string kind, bool succeeded, long bytes, TimeSpan elapsed)
    {
        var tags = new TagList { Tenant(tenantId), new("kind", kind), Outcome(succeeded) };
        _mediaProcessed.Add(1, tags);
        _mediaDuration.Record(elapsed.TotalSeconds, tags);
        if (bytes > 0)
        {
            _mediaBytes.Add(bytes, Tenant(tenantId), new("kind", kind));
        }
    }

    public void SiteBuild(Guid tenantId, string mode, bool succeeded, TimeSpan elapsed)
    {
        var tags = new TagList { Tenant(tenantId), new("mode", mode), Outcome(succeeded) };
        _siteBuilds.Add(1, tags);
        _siteBuildDuration.Record(elapsed.TotalSeconds, tags);
    }

    public void EmailSent() => _emailSent.Add(1);

    /// <param name="reason">A bounded class — "rejected", "transient", "invalid-address" —
    /// never the relay's message, which is unbounded free text.</param>
    public void EmailFailed(string reason) => _emailFailed.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void AiCall(Guid tenantId, string provider, string model, long promptTokens, long completionTokens, TimeSpan elapsed)
    {
        var baseTags = new TagList { Tenant(tenantId), new("provider", provider), new("model", model) };

        var prompt = baseTags;
        prompt.Add("direction", "input");
        _aiTokens.Add(promptTokens, prompt);

        var completion = baseTags;
        completion.Add("direction", "output");
        _aiTokens.Add(completionTokens, completion);

        _aiDuration.Record(elapsed.TotalSeconds, baseTags);
    }

    public void ChatMessage(Guid tenantId, string senderKind) =>
        _chatMessages.Add(1, Tenant(tenantId), new("sender", senderKind));

    public void FormSubmission(Guid tenantId) => _formSubmissions.Add(1, Tenant(tenantId));

    public void SearchQuery(Guid tenantId) => _searchQueries.Add(1, Tenant(tenantId));

    /// <param name="method">"password", "google", "refresh".</param>
    /// <param name="outcome">"succeeded", "failed", "lockedout".</param>
    public void Login(string method, string outcome) =>
        _logins.Add(1, new KeyValuePair<string, object?>("method", method), new("outcome", outcome));

    public void Signup(string method) => _signups.Add(1, new KeyValuePair<string, object?>("method", method));

    public void TenantCreated() => _tenantsCreated.Add(1);

    public void PluginInstanceChanged(string pluginId, string action) =>
        _pluginInstanceChanges.Add(1, new KeyValuePair<string, object?>("plugin", pluginId), new("action", action));

    /// <summary>
    /// Publish the current depth of one outbox. Called by whoever polls it, on every poll
    /// including the empty ones — a depth that stops being reported reads as a flat line at
    /// its last value, which is indistinguishable from a stuck dispatcher.
    /// </summary>
    public void OutboxDepth(string outbox, long depth) => _outboxDepths[outbox] = depth;

    private IEnumerable<Measurement<long>> ObserveOutboxDepths()
    {
        foreach (var (outbox, depth) in _outboxDepths)
        {
            yield return new Measurement<long>(depth, new KeyValuePair<string, object?>("outbox", outbox));
        }
    }

    public void OutboxLag(string outbox, TimeSpan lag) =>
        _outboxLag.Record(lag.TotalSeconds, new KeyValuePair<string, object?>("outbox", outbox));

    public void MessageHandled(string stream, string subject, bool succeeded, TimeSpan elapsed)
    {
        var tags = new TagList { new("stream", stream), new("subject", subject), Outcome(succeeded) };
        _messagesConsumed.Add(1, tags);
        _messageHandlingDuration.Record(elapsed.TotalSeconds, tags);
    }

    /// <summary>
    /// Tenant id, not slug: a slug can be renamed and would then split one tenant's history
    /// into two series that never rejoin.
    /// </summary>
    private static KeyValuePair<string, object?> Tenant(Guid tenantId) =>
        new("tenant_id", tenantId == Guid.Empty ? "platform" : tenantId.ToString());

    private static KeyValuePair<string, object?> Outcome(bool succeeded) =>
        new("outcome", succeeded ? "success" : "failure");

    public void Dispose() => _meter.Dispose();
}
