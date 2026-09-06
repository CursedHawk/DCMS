using System.Security.Claims;
using Dcms.AdminApi.Ai;
using Dcms.AdminApi.ApiClientGen;
using Dcms.AdminApi.Analytics;
using Dcms.AdminApi.Audit;
using Dcms.AdminApi.Observability;
using Dcms.AdminApi.Chat;
using Dcms.AdminApi.Cms;
using Dcms.AdminApi.Forms;
using Dcms.AdminApi.Media;
using Dcms.AdminApi.Notifications;
using Dcms.AdminApi.Openapi;
using Dcms.AdminApi.Plugins;
using Dcms.AdminApi.Tenancy;
using Dcms.Plugins.All;
using Dcms.PluginSdk.Runtime;
using Dcms.Shared.Caching;
using Dcms.AdminApi.Sites;
using Dcms.AdminApi.Social;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Analytics;
using Dcms.Shared.Data.DataProtection;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Notifications;
using Dcms.Shared.Data.Platform;
using Dcms.Shared.Data.Social;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Search;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.Visitors;
using Dcms.Shared.Hosting;
using Dcms.Shared.Media;
using Dcms.Shared.Messaging;
using Dcms.Shared.Messaging.Email;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Dcms.Shared.Storage;
using Dcms.Shared.Vault;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("admin-api");
builder.Services.AddDcmsMessaging(builder.Configuration);
builder.Services.AddDcmsCaching(builder.Configuration);
builder.Services.AddDcmsEmailQueue();
builder.Services.AddDcmsResourceAuthentication(builder.Configuration);

// The notification hub's token arrives in the query string: the WebSocket transport cannot
// set an Authorization header. Scoped to /api/hub so a leaked URL from anywhere else in the
// API is not a way to authenticate with a token in a log line or a Referer.
builder.Services.Configure<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(
    Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme, jwt =>
    {
        jwt.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/api/hub"))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            },
        };
    });

// Notification fan-out across replicas. The channel prefix MUST differ from content-api's
// "dcms-chat": both services share one Redis, and a shared prefix would cross-deliver
// between the two hubs.
var notificationSignalR = builder.Services.AddSignalR();
var signalRRedis = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(signalRRedis))
{
    notificationSignalR.AddStackExchangeRedis(signalRRedis + ",abortConnect=false",
        options => options.Configuration.ChannelPrefix =
            StackExchange.Redis.RedisChannel.Literal("dcms-notify"));
}

// Tenancy: shared TenancyDbContext + header-based tenant resolution.
builder.Services.AddHttpContextAccessor();
builder.Services.AddDcmsObjectStorage(builder.Configuration);
builder.Services.AddDcmsTenancyData(builder.Configuration);
builder.Services.AddDcmsCmsData(builder.Configuration);
builder.Services.AddDcmsMediaData(builder.Configuration);
builder.Services.AddDcmsSitesData(builder.Configuration);
builder.Services.AddDcmsAiData(builder.Configuration);
builder.Services.AddDcmsSocialData(builder.Configuration);
// Shared by the upload endpoint and the Meta feed sync -- see MediaIngestService.
builder.Services.AddScoped<Dcms.AdminApi.Media.MediaIngestService>();
builder.Services.AddDcmsSearchData(builder.Configuration);
builder.Services.AddDcmsAnalyticsData(builder.Configuration);
builder.Services.AddDcmsVisitorsData(builder.Configuration);
builder.Services.AddDcmsChatData(builder.Configuration);
builder.Services.AddDcmsFormsData(builder.Configuration);
builder.Services.AddDcmsNotificationsData(builder.Configuration);
builder.Services.AddDcmsAuditData(builder.Configuration);
// admin-api owns no platform-console data and reads none of it. It registers the context
// solely so the migrate job (this image, --migrate-only) creates the schema as the owner;
// platform-api then reaches the rows through the least-privilege dcms_platform role.
builder.Services.AddDcmsPlatformData(builder.Configuration);
// Registered so the migration job can create the shared Data Protection key ring.
// admin-api does not consume it -- it authenticates with bearer tokens and sets no
// cookies -- but it owns the DDL for every schema in this database.
builder.Services.AddDcmsDataProtection(builder.Configuration);
// Same reason again: the edge's certificate, ACME-account and route tables are created by this
// image running --migrate-only. admin-api reads none of them today; Phase 5 gives it the
// certificate-status and custom-upload endpoints, which is when it starts to.
builder.Services.AddDcmsEdgeData(builder.Configuration);
builder.Services.AddDcmsVaultTransit();
builder.Services.AddScoped<AiPromptBuilder>();
builder.Services.AddDcmsTenantResolutionByHeader();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<TenantProvisioning>();
builder.Services.AddHostedService<TenancyMigrator>();

// Plugin catalog (manifests only — no plugin runtime services) + config validation.
builder.Services.AddDcmsPluginCatalog(plugins => plugins.AddAll());
builder.Services.AddSingleton<PluginConfigValidator>();

// Permission evaluation: dynamic policy + tenancy-backed, Redis-cached resolver.
builder.Services.AddDcmsPermissionAuthorization();
builder.Services.AddScoped<TenancyPermissionResolver>();
builder.Services.AddScoped<IPermissionResolver>(sp => sp.GetRequiredService<TenancyPermissionResolver>());

builder.Services.AddSingleton<IDnsTxtLookup, DnsTxtLookup>();
builder.Services.AddHostedService<MembershipChangedConsumer>();
builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddHostedService<ScheduledPublishWorker>();
builder.Services.AddHostedService<AnalyticsConsumer>();
// analytics.events had no retention at all: every pageview from every tenant site accumulated
// forever on a host with 40 GB free. The daily rollups next to it already hold the aggregate.
builder.Services.AddHostedService<AnalyticsRetentionWorker>();
builder.Services.AddHostedService<ChatFanoutConsumer>();

// In-app notifications. Each consumer turns one already-published subject into a
// notification, so the producing services are untouched. All are shared durables: the work
// is a database write that exactly one replica must do, and the SignalR Redis backplane is
// what carries the push to browsers connected to the others.
builder.Services.AddScoped<INotificationPublisher, NotificationPublisher>();
builder.Services.AddHostedService<SitePublishedNotificationConsumer>();
builder.Services.AddHostedService<SiteBuildFailedNotificationConsumer>();
builder.Services.AddHostedService<MediaProcessedNotificationConsumer>();
builder.Services.AddHostedService<MediaFailedNotificationConsumer>();
builder.Services.AddHostedService<ContentPublishedNotificationConsumer>();
builder.Services.AddHostedService<ContentUnpublishedNotificationConsumer>();
builder.Services.AddHostedService<DomainVerifiedNotificationConsumer>();
builder.Services.AddHostedService<PluginInstanceNotificationConsumer>();
builder.Services.AddHostedService<NotificationIngestConsumer>();
// Invitation expiry is the one source with no event behind it -- it is a passive column
// that nothing has ever swept. See InvitationExpiryWorker.
builder.Services.AddHostedService<InvitationExpiryWorker>();
builder.Services.AddHostedService<NotificationRetentionWorker>();
builder.Services.AddHostedService<Dcms.AdminApi.Audit.AuditChainWriter>();
// Brings in the records from the two services that cannot reach the audit schema, so
// everything still reaches the chain by one path.
builder.Services.AddHostedService<Dcms.AdminApi.Audit.AuditIngestConsumer>();
// Seals finished months, keeps partitions ahead of the writer, drops what retention has
// expired, and publishes the numbers that say whether any of it is working.
builder.Services.AddHostedService<Dcms.AdminApi.Audit.AuditMaintenanceWorker>();
// Reads audit.recorded — the fan-out subject the chain writer has always published and
// nothing consumed — and projects each record onto the log pipeline, giving the audit log a
// searchable presence next to the traces it shares a TraceId with.
builder.Services.AddHostedService<Dcms.AdminApi.Audit.AuditLogProjector>();

// Outbound client-credentials token provider for calling ai-gateway.
builder.Services.Configure<ServiceClientOptions>(builder.Configuration.GetSection(ServiceClientOptions.SectionName));

// Grafana posts fired alerts to /api/internal/alerts, which puts them on the EMAIL work
// queue. Configuring Grafana's own SMTP instead would put the relay credentials in a second
// container; email-worker is deliberately the only thing that speaks to the relay.
builder.Services.Configure<Dcms.AdminApi.Observability.AlertingOptions>(
    builder.Configuration.GetSection("Alerting"));
builder.Services.AddHttpClient<IServiceTokenProvider, ServiceTokenClient>();
builder.Services.AddHttpClient("ai-gateway", (sp, client) =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["Services:AiGateway"] ?? "http://localhost:5007";
    client.BaseAddress = new Uri(baseUrl);
});

// Reverse-proxy target for the site preview (delivery API). Same content-api the
// published site talks to; the preview proxy injects the tenant + sandbox headers.
builder.Services.AddHttpClient("content-api", (sp, client) =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["Services:ContentApi"] ?? "http://content-api:8080";
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
});

// Forgejo git server: source of truth for Mode B site source. The machine token
// arrives via Vault (secret/dcms/admin-api, Forgejo__Token); git operations are
// no-ops until it's set (ForgejoOptions.Enabled).
// The Meta OAuth callback is necessarily anonymous -- Meta redirects a browser to it with no
// bearer token -- so it is the one unauthenticated write path on the admin plane. The state
// token is 256 bits of randomness and guessing it is hopeless, but each guess still costs a
// database lookup, so the endpoint gets its own limiter. A NAMED policy rather than a global
// one: admin-api has never had rate limiting, and quietly throttling the whole admin SPA while
// adding a social feature would be a surprising way to find that out.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy(Dcms.AdminApi.Social.MetaOAuthEndpoints.CallbackRateLimitPolicy, http =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// Meta (Facebook/Instagram) social feeds. Absent credentials mean the feature simply does not
// offer itself -- same shape as Google SSO in identity, so dev needs no Meta app.
builder.Services.Configure<Dcms.AdminApi.Social.MetaSocialOptions>(
    builder.Configuration.GetSection(Dcms.AdminApi.Social.MetaSocialOptions.SectionName));
builder.Services.AddHttpClient<Dcms.AdminApi.Social.MetaOAuthClient>(client =>
{
    // Every URL is absolute (the two login paths live on different hosts), so no BaseAddress.
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<Dcms.AdminApi.Social.MetaGraphClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
// Downloads from Meta's CDN. A longer timeout than the Graph calls (these are files, not
// JSON) but still bounded, because a stalled download must not hold a sync pass open.
builder.Services.AddHttpClient(Dcms.AdminApi.Social.MetaMediaMirror.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
});
builder.Services.AddScoped<Dcms.AdminApi.Social.MetaFeedFetcher>();
builder.Services.AddScoped<Dcms.AdminApi.Social.MetaMediaMirror>();
builder.Services.AddScoped<Dcms.AdminApi.Social.MetaFeedSyncService>();
// Off by default in tests and anywhere without a Meta app: an enabled worker with no
// credentials would just log failures every minute.
if (builder.Configuration.GetValue("Social:SyncEnabled", true))
{
    builder.Services.AddHostedService<Dcms.AdminApi.Social.MetaSyncWorker>();
    // Gated on the same switch. A Meta long-lived token cannot be renewed once it has
    // expired, so without this every connected account stops about sixty days after it was
    // connected -- and turning syncing off is exactly when nobody would notice.
    builder.Services.AddHostedService<Dcms.AdminApi.Social.MetaTokenRefreshWorker>();
}

builder.Services.Configure<Dcms.AdminApi.Sites.Git.ForgejoOptions>(
    builder.Configuration.GetSection(Dcms.AdminApi.Sites.Git.ForgejoOptions.SectionName));

// Prod safety: the git push webhook (/api/internal/git/webhook) is anonymous and
// gated only by an HMAC over Forgejo__WebhookSecret. An empty or well-known default
// secret lets anyone forge webhooks and queue arbitrary builds, so refuse to start
// in Production when git integration is enabled but the secret is weak. Mirrors the
// DCMS_REFUSE_DEV_VAULT guard in Dcms.Shared.Vault.
if (builder.Environment.IsProduction())
{
    var forgejo = builder.Configuration
        .GetSection(Dcms.AdminApi.Sites.Git.ForgejoOptions.SectionName)
        .Get<Dcms.AdminApi.Sites.Git.ForgejoOptions>() ?? new();
    var weakSecrets = new HashSet<string>(StringComparer.Ordinal)
    {
        "dcms-dev-webhook-secret", "dcms-forgejo-webhook",
    };
    if (forgejo.Enabled &&
        (string.IsNullOrWhiteSpace(forgejo.WebhookSecret) || weakSecrets.Contains(forgejo.WebhookSecret.Trim())))
    {
        throw new InvalidOperationException(
            "Refusing to start: Forgejo__WebhookSecret is empty or a known default in Production. " +
            "Set a strong, unique FORGEJO_WEBHOOK_SECRET and re-register the site webhooks.");
    }

    // Same shape for the alert webhook (/api/internal/alerts), which can send mail from the
    // platform's own address — a forged post there is a phishing primitive, not just noise.
    //
    // Empty is deliberately allowed and not checked here: the endpoint already refuses every
    // request when the secret is unset, so an installation that never configured alerting
    // fails closed rather than failing to start. What is refused is a secret that is present
    // but guessable, which fails open and looks configured.
    var alerting = builder.Configuration.GetSection("Alerting").Get<Dcms.AdminApi.Observability.AlertingOptions>() ?? new();
    var weakAlertSecrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "changeme", "secret", "alert", "dcms-alert", "dcms-dev-alert-secret", "grafana",
    };
    if (!string.IsNullOrWhiteSpace(alerting.WebhookSecret)
        && (alerting.WebhookSecret.Trim().Length < 16 || weakAlertSecrets.Contains(alerting.WebhookSecret.Trim())))
    {
        throw new InvalidOperationException(
            "Refusing to start: Alerting__WebhookSecret is too short or a known default in Production. " +
            "Set a strong, unique ALERT_WEBHOOK_SECRET (openssl rand -base64 32) or leave it unset to disable alert delivery.");
    }
}
builder.Services.AddHttpClient<Dcms.AdminApi.Sites.Git.ForgejoClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Dcms.AdminApi.Sites.Git.ForgejoOptions>>().Value;
    client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
    if (!string.IsNullOrWhiteSpace(opts.Token))
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", opts.Token);
});
builder.Services.AddScoped<Dcms.AdminApi.Sites.Git.SiteGitService>();
builder.Services.AddScoped<Dcms.AdminApi.Sites.Git.RepoAccessReconciler>();
builder.Services.AddScoped<Dcms.AdminApi.Sites.SiteDeleter>();
// Live deployment/commit fan-out to everyone with a site open in the IDE. Singleton because
// it holds nothing per-request -- IHubContext is itself a singleton -- and because the site
// event consumers reach it from background scopes.
builder.Services.AddSingleton<Dcms.AdminApi.Sites.ISiteLiveUpdates, Dcms.AdminApi.Sites.SiteLiveUpdates>();
builder.Services.AddScoped<TenantDeleter>();

var app = builder.Build();

// One-shot migration mode. The deploy pipeline runs `admin-api --migrate-only` as a job
// before rolling any service, so DDL happens exactly once, from one process, with nothing
// serving traffic against a half-migrated schema.
//
// Nothing is hosted in this mode: app.Run() is never reached, so no consumer binds, no
// timer starts and no port is opened. It runs the migrations and exits, and a non-zero
// exit stops the deploy.
if (args.Contains("--migrate-only"))
{
    using var migrationScope = app.Services.CreateScope();
    var migrationLogger = migrationScope.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("Dcms.Migrate");

    // Deliberately bypasses the Tenancy:Migrate gate: that flag says "do not migrate on
    // startup", and this is not a startup.
    DcmsMigrationRunner.AssertRlsCoverage(migrationScope.ServiceProvider, migrationLogger);
    await DcmsMigrationRunner.RunAsync(
        migrationScope.ServiceProvider, app.Configuration, migrationLogger, CancellationToken.None);

    migrationLogger.LogInformation("Migration job complete.");
    return;
}

// Behind the TLS edge admin-api is reached over plain HTTP on the internal
// network, so Request.Scheme would be "http". Honour X-Forwarded-Proto/Host so
// absolute links we mint for users (invitation accept links) point at the public
// https origin. admin-api is only reachable through the edge, so all proxies are
// trusted.
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost,
};
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
// First in the pipeline, so an exception anywhere below it becomes a ProblemDetails
// carrying the trace id instead of a bare Kestrel 500 with no body and nothing to quote.
app.UseDcmsProblemDetails();

app.UseForwardedHeaders(forwardedHeaders);

// After UseForwardedHeaders (so the client address is the caller's) and before
// authentication (so refusals from the auth stack are still inside the scope).
app.UseDcmsAudit();

app.UseAuthentication();
app.UseMultiTenant();
// Between resolution and authorization: the tenant is now known, so membership can be
// checked, and the endpoints that survive still see a tenant context. Without this, an
// endpoint guarded by a bare RequireAuthorization() trusts X-Dcms-Tenant outright.
app.UseTenantMembership();
// Beside the membership check, and for the same reason: a client-credentials token has no user
// behind it, so the membership check waves it through and a bare RequireAuthorization() then
// accepts it. This confines a service token to the endpoints that named its scope.
app.UseServicePrincipalGuard();
app.UseAuthorization();

// Only endpoints that opt in with RequireRateLimiting are affected; there is no global limiter.
app.UseRateLimiter();

app.MapDcmsDefaultEndpoints();
app.MapTenancyEndpoints();
app.MapTenantAdminEndpoints();
app.MapMyAccountEndpoints();
app.MapInvitationEndpoints();
app.MapDomainEndpoints();
app.MapDomainCertificateEndpoints();
// The platform's OWN certificates, from the platform console. Here rather than in platform-api
// because admin-api already owns and migrates the edge schema; see ADR 0011.
app.MapManagedCertificateEndpoints();
app.MapPluginEndpoints();
app.MapContentEndpoints();
app.MapMediaEndpoints();
app.MapSiteEndpoints();
app.MapSiteDeletion();
app.MapNotificationEndpoints();
// Mounted under /api so it rides the existing edge route to admin-api -- no route change,
// and no matcher ordering against content-api's /hub/* for the chat hub.
app.MapHub<NotificationHub>("/api/hub/notifications")
    .AuditExempt("SignalR transport endpoint, not an action. The notifications it carries "
               + "are records of actions that were audited where they happened.");
// Same reasoning, and the same /api mount so it rides the existing edge route: live build and
// commit state for one site's workspace. See SiteHub.
app.MapHub<Dcms.AdminApi.Sites.SiteHub>("/api/hub/sites")
    .AuditExempt("SignalR transport endpoint, not an action. The builds and commits it "
               + "carries are audited where they are performed.");
app.MapAuditEndpoints();
app.MapAnalyticsPruneEndpoints();
app.MapAlertEndpoints();
app.MapSitePreview();
app.MapFormSubmissionEndpoints();
app.MapAiSettingsEndpoints();
app.MapMetaOAuthEndpoints();
app.MapMetaStoriesEndpoints();
app.MapAiGenerationEndpoints();
app.MapAiAgentEndpoints();
app.MapAnalyticsDashboard();
app.MapChatConsole();
app.MapOpenApiPreview();
app.MapApiClientDownload();
app.MapSiteScaffoldEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "admin-api" }));

// Current admin identity — proves the SPA's access token validates here.
app.MapGet("/api/admin/me", (ClaimsPrincipal user) => Results.Ok(new
{
    sub = user.FindFirstValue("sub"),
    name = user.FindFirstValue("name"),
    email = user.FindFirstValue("email"),
    roles = user.FindAll("role").Select(c => c.Value),
})).RequireAuthorization().AllowNonMemberTenant(AllowNonMemberTenantAttribute.SelfScoped);

// Proves the service-to-service client-credentials flow end to end:
// admin-api obtains a dcms.ai token and calls ai-gateway's protected ping.
app.MapGet("/api/admin/ai/ping", async (
    IServiceTokenProvider tokens,
    IHttpClientFactory httpClientFactory,
    CancellationToken ct) =>
{
    var token = await tokens.GetTokenAsync(DcmsScopes.Ai, ct);
    var client = httpClientFactory.CreateClient("ai-gateway");
    client.DefaultRequestHeaders.Authorization = new("Bearer", token);
    var response = await client.GetAsync("/internal/ping", ct);
    var body = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
}).RequireAuthorization();

app.Run();

public partial class Program;

/// <summary>Scope names requested for outbound service calls.</summary>
file static class DcmsScopes
{
    public const string Ai = "dcms.ai";
}
