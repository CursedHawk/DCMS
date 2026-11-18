using Dcms.AdminApi.Environments;
using Dcms.Shared.Caching.Idempotency;
using System.Security.Claims;
using Dcms.AdminApi.Ai;
using Dcms.AdminApi.ApiClientGen;
using Dcms.AdminApi.Analytics;
using Dcms.AdminApi.Audit;
using Dcms.AdminApi.Jobs;
using Dcms.AdminApi.Observability;
using Dcms.AdminApi.Cms;
using Dcms.AdminApi.Media;
using Dcms.AdminApi.Notifications;
using Dcms.AdminApi.Openapi;
using Dcms.AdminApi.Plugins;
using Dcms.AdminApi.Tenancy;
using Dcms.AdminApi.Tenancy.Usage;
using Dcms.Plugins.All;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.PluginSdk.Runtime.Data;
using Dcms.PluginSdk.Runtime.Hosting;
using Dcms.PluginSdk.Runtime.Platform;
using Dcms.Shared.Caching;
using Dcms.AdminApi.Sites;
using Dcms.AdminApi.Sites.Git;
using Dcms.AdminApi.Views;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Analytics;
using Dcms.Shared.Data.DataProtection;
using Dcms.Shared.Data.DynamicApps;
using Dcms.Shared.Data.Functions;
using Dcms.Shared.Data.UserAuth;
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
using Dcms.Shared.AdminPlane;
using Dcms.Shared.Jobs;
using Dcms.Shared.Media;
using Dcms.Shared.Messaging;
using Dcms.Shared.Messaging.Email;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Dcms.Shared.Security.Realms;
using Dcms.Shared.Storage;
using Dcms.Shared.Vault;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Microsoft.AspNetCore.HttpOverrides;
using Dcms.Shared.Data.Rls;

var builder = WebApplication.CreateBuilder(args);
// admin-api serves the console's requests and holds its sockets, and nothing else: every piece
// of background work -- job lanes, plugin workers, event consumers, outboxes, sweeps -- is
// jobs-worker's, a separate service and image (ADR 0036).
const string serviceName = "admin-api";
builder.AddDcmsServiceDefaults(serviceName);
// What admin-api shares with jobs-worker: data, messaging, the SignalR backplane, plugins (without
// their background work), and the domain services both reach for (ADR 0036).
builder.AddDcmsAdminPlane(serviceName, runsBackgroundWork: false);
builder.Services.AddDcmsResourceAuthentication(builder.Configuration);

// The hubs' token used to arrive in the query string, because a WebSocket handshake cannot set
// an Authorization header. ADR 0014 phase 5 removed that: the edge sets one on the operator's
// behalf as it proxies the handshake, which a browser cannot do for itself. What goes with it
// is a token in a URL — and therefore in an access log, a Referer and anything that samples
// either.
//
// The hubs are reachable no other way: /api is a BFF route, so the edge strips a
// client-supplied Authorization header there and dcms-admin-spa no longer holds a scope that
// would let a browser obtain a token for this API at all.

// Pushes to the browsers connected to this replica, so it belongs where the sockets are.
builder.Services.AddHostedService<JobLiveUpdates>();
// admin-api owns no platform-console data and reads none of it. It registers the context
// solely so the migrate job (this image, --migrate-only) creates the schema as the owner;
// platform-api then reaches the rows through the least-privilege dcms_platform role.
builder.Services.AddDcmsPlatformData(builder.Configuration);
// One resolution of the tenant's content-API document for every generator (see ApiClientGen).
builder.Services.AddScoped<Dcms.AdminApi.ApiClientGen.TenantApiResolver>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<Dcms.AdminApi.Views.ViewContext>();
// Who may act on platform-wide state: a SuperAdmin here, or the console's API acting for one.
builder.Services.AddScoped<ConsoleCaller>();
builder.Services.AddHostedService<TenancyMigrator>();

// Permission evaluation: the dynamic policy (the resolver behind it is the shared plane's).
builder.Services.AddDcmsPermissionAuthorization();

builder.Services.AddSingleton<DnsTxtLookup>();

// Grafana posts fired alerts to /api/internal/alerts, which puts them on the EMAIL work
// queue. Configuring Grafana's own SMTP instead would put the relay credentials in a second
// container; email-worker is deliberately the only thing that speaks to the relay.
builder.Services.Configure<Dcms.AdminApi.Observability.AlertingOptions>(
    builder.Configuration.GetSection("Alerting"));
// Named rate-limit policies only (plugins add theirs -- the Meta OAuth callback). No global
// limiter: admin-api has never had one, and quietly throttling the whole admin SPA would be a
// surprising way to find that out.
builder.Services.AddRateLimiter(limiter => limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests);

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
// One Idempotency-Key, one operation (ADR 0024). Required of people -- the console always sends
// one -- and honoured for services. Tests that do not exercise it turn the requirement off.
app.UseDcmsIdempotency("admin-api", app.Configuration.GetValue("Idempotency:RequireForUsers", true));
app.UseMultiTenant();
// ADR 0028: the environment the request names, after the tenant it belongs to.
app.UseDcmsEnvironment();
// Between resolution and authorization: the tenant is now known, so membership can be
// checked, and the endpoints that survive still see a tenant context. Without this, an
// endpoint guarded by a bare RequireAuthorization() trusts X-Dcms-Tenant outright.
app.UseTenantMembership();
// Configuration an environment leaves to deployments is not changed directly (ADR 0026).
app.UseMiddleware<Dcms.AdminApi.Environments.EnvironmentOwnershipMiddleware>();
// Beside the membership check, and for the same reason: a client-credentials token has no user
// behind it, so the membership check waves it through and a bare RequireAuthorization() then
// accepts it. This confines a service token to the endpoints that named its scope.
app.UseServicePrincipalGuard();
// Immediately after the guard, so it only ever sees a service the endpoint invited: restores
// the operator a peer acted on behalf of, so the audit log names a person rather than the last
// hop. Authorization is untouched -- see PropagatedActorMiddleware.
app.UsePropagatedActor();
app.UseAuthorization();

// Only endpoints that opt in with RequireRateLimiting are affected; there is no global limiter.
app.UseRateLimiter();

app.MapDcmsDefaultEndpoints();
app.MapTenancyEndpoints();
app.MapTenantAdminEndpoints();
app.MapEnvironmentEndpoints();
app.MapEnvironmentMigrationEndpoints();
app.MapTenantUsageEndpoints();
app.MapMyAccountEndpoints();
app.MapInvitationEndpoints();
app.MapDomainEndpoints();
app.MapDomainCertificateEndpoints();
// The platform's OWN certificates, from the platform console. Here rather than in platform-api
// because admin-api already owns and migrates the edge schema; see ADR 0011.
app.MapManagedCertificateEndpoints();
app.MapRateLimitExemptionEndpoints();
app.MapPluginEndpoints();
Dcms.AdminApi.Connections.ApiConnectionEndpoints.MapApiConnectionEndpoints(app);
// Plugins' own admin routes, /api/admin/plugins/{slug}/..., for authenticated tenant members
// (UseTenantMembership above has already confirmed membership of X-Dcms-Tenant).
app.MapDcmsPluginAdminEndpoints().RequireAuthorization();
app.MapDcmsPluginHostEndpoints();
// Contract operations for members and AI agents (docs/adr/0016).
app.MapDcmsContractAdminEndpoints();
// Plugins' data sets for the console's plugin pages (docs/adr/0018).
app.MapDcmsPluginDataEndpoints();
// Plugins' own admin screens: what they declare, and installed plugins' UI files (docs/adr/0019).
app.MapDcmsPluginUiEndpoints();
app.MapNavigationEndpoints();
app.MapMarketplaceEndpoints();
app.MapContentEndpoints();
app.MapContentListEndpoints();
app.MapMediaEndpoints();
app.MapGoogleDriveImport();
app.MapSiteEndpoints();
app.MapRepoEndpoints();
app.MapPublishBranchEndpoints();
app.MapSiteSearchEndpoints();
Dcms.AdminApi.Sites.Audits.SiteAuditEndpoints.MapSiteAuditEndpoints(app);
app.MapTableViewEndpoints();
app.MapSiteDeletion();
app.MapNotificationEndpoints();
app.MapPlatformNotificationEndpoints();
app.MapJobEndpoints();
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
app.MapAiSettingsEndpoints();
app.MapAiAgentPolicyEndpoints();
app.MapAiKnowledgeEndpoints();
app.MapAiAgentEndpoints();
app.MapAiConversationEndpoints();
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
