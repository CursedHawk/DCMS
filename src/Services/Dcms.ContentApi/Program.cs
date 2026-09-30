using Dcms.ContentApi.Delivery;
using Dcms.ContentApi.Plugins;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Platform;
using Dcms.Plugins.All;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Caching;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Hosting;
using Dcms.Plugins.VisitorAuth;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.DataProtection;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Search;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.Visitors;
using Dcms.Shared.Messaging;
using Dcms.Shared.Messaging.Email;
using Dcms.Shared.Security;
using Dcms.Shared.Storage;
using Dcms.Shared.Vault;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Dcms.Shared.Data.Rls;

var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("content-api");
builder.Services.AddDcmsMessaging(builder.Configuration);
builder.Services.AddDcmsCaching(builder.Configuration);
builder.Services.AddDcmsObjectStorage(builder.Configuration);

// Tenant resolution: header in Phase 4 (host/domain routing arrives with
// site-host in Phase 8). Reads the tenancy + CMS schemas owned by admin-api.
builder.Services.AddDcmsTenancyData(builder.Configuration);
builder.Services.AddDcmsAuditData(builder.Configuration);
// ADR 0015: sets app.tenant_id / app.scope per unit of work when Rls:Enforce is on; absent otherwise.
builder.Services.AddDcmsRlsEnforcement(builder.Configuration);
builder.Services.AddDcmsCmsData(builder.Configuration);
builder.Services.AddDcmsMediaData(builder.Configuration);
builder.Services.AddDcmsFormsData(builder.Configuration);

// Optional per-form email notifications. Rendered here, delivered by email-worker
// off the EMAIL work queue — content-api never touches SMTP.
builder.Services.AddDcmsEmailQueue();
builder.Services.AddDcmsSearchData(builder.Configuration);
builder.Services.AddDcmsVisitorsData(builder.Configuration);
builder.Services.AddDcmsChatData(builder.Configuration);

// Data Protection, persisted to Postgres and shared with every other service.
//
// content-api is the public delivery plane and it hosts the chat hub, which is what makes
// this load-bearing rather than tidy: SignalR protects the connection token it hands out at
// /negotiate with an IDataProtector. With the default in-container key ring, a negotiate
// answered by one replica produces a token no other replica can unprotect, so the follow-up
// connect fails -- and it fails as an ordinary reconnect, so it reads as a flaky websocket
// rather than as a configuration fault. The same ring is recreated on every container
// restart, which turns one deploy into a round of failed reconnects even at a single replica.
//
// The encryptor that wraps the ring resolves ITransitEncryptor, so the Vault client is
// registered unconditionally and the flag decides only whether keys are WRITTEN wrapped. A
// service reading the shared ring with the flag still off would otherwise meet a wrapped row
// it had no client to decrypt. Registering costs nothing on its own -- the client logs in
// lazily and only talks to Vault when asked to encrypt or decrypt.
builder.Services.AddDcmsVaultTransit();
builder.Services.AddDcmsDataProtection(builder.Configuration);

// Prod safety, same shape as admin-api's webhook-secret guards. Visitor:SigningKey is a
// manual `vault kv put` in the deploy guide — infra/vault/init.sh only writes a placeholder
// to secret/dcms/content-api — so the way this goes wrong is a step being skipped, not a bad
// value being chosen. The whole tenant binding in a visitor token is its audience, and the
// tenant id is not a secret, so an unconfigured key means every visitor session on every
// tenant is forgeable by anyone who has read this repository. Fail loudly instead.
if (builder.Environment.IsProduction())
{
    var visitor = builder.Configuration.GetSection(VisitorTokenOptions.SectionName).Get<VisitorTokenOptions>()
                  ?? new VisitorTokenOptions();
    var key = visitor.SigningKey?.Trim() ?? string.Empty;
    if (key.Length == 0
        || key == VisitorTokenOptions.DevelopmentSigningKey
        || System.Text.Encoding.UTF8.GetByteCount(key) < VisitorTokenOptions.MinimumKeyBytes)
    {
        throw new InvalidOperationException(
            "Refusing to start: Visitor__SigningKey is unset, the development default, or shorter "
            + $"than {VisitorTokenOptions.MinimumKeyBytes} bytes in Production. Set a strong, unique "
            + "value (openssl rand -base64 32) at secret/dcms/content-api.");
    }
}
builder.Services.AddDcmsTenantResolutionByHeader();

// Preview sandbox: the admin-api preview proxy sets X-Dcms-Sandbox so a site
// preview's writes (forms, visitors, chat) land in the per-tenant sandbox space.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Dcms.Shared.Kernel.Abstractions.ISandboxContext, Dcms.ContentApi.HeaderSandboxContext>();

// Platform-user authentication for the chat hub's agent role. Visitors connect anonymously and
// always did; only an agent presents a platform JWT.
//
// That JWT used to ride in the access_token query string, because a WebSocket handshake cannot
// set a header. Since ADR 0014 phase 5 the edge sets one as it proxies the agent's handshake
// (/hub on the admin host is a BFF route), so the query-string path is gone and with it a token
// in a URL. Visitors are unaffected: the site chat widget has never sent a token at all.
builder.Services.AddDcmsResourceAuthentication(builder.Configuration);

builder.Services.AddDcmsRateLimiting(builder.Configuration);

// No default policy, deliberately. There used to be one -- an origin list that allowed
// credentials -- and the last thing that needed it was the admin console reaching the
// chat hub cross-origin. Nothing does any more: Vite proxies /api and /hub in dev, the edge
// routes them in production, and site-host proxies /api/* for tenant sites, so every browser
// that talks to this service is same-origin and never asks for CORS at all.
//
// Removing it rather than leaving it harmless. A credentialed policy is the surface that
// turns a cookie into a cross-origin capability, and content-api is the internet-facing
// service; keeping one that nothing uses means the next person to add a cookie here inherits
// it without deciding to. The cross-origin surface is the plugins' anonymous policies, each
// registered by the plugin that owns the endpoint (analytics beacon, form submit, branding read).
builder.Services.AddCors();

// Outbound client-credentials token provider + ai-gateway client, used by the AI
// Chatbot to generate visitor replies. Reuses the shared dcms.ai service client.
builder.Services.Configure<ServiceClientOptions>(builder.Configuration.GetSection(ServiceClientOptions.SectionName));
builder.Services.AddHttpClient<IServiceTokenProvider, ServiceTokenClient>();
builder.Services.AddHttpClient("ai-gateway", (sp, client) =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["Services:AiGateway"] ?? "http://localhost:5007";
    client.BaseAddress = new Uri(baseUrl);
});

builder.AddDcmsPlugins(PluginPlane.Site, plugins => plugins.AddAll());
builder.Services.AddHostedService<ContentCacheInvalidator>();
builder.Services.AddHostedService<SearchIndexer>();

var app = builder.Build();
// First in the pipeline, so an exception anywhere below it becomes a ProblemDetails
// carrying the trace id instead of a bare Kestrel 500 with no body and nothing to quote.
app.UseDcmsProblemDetails();

// Before the rate limiter, which partitions on Connection.RemoteIpAddress, and before the
// audit middleware, which records it. content-api is reached only through the edge (for /hub on
// the admin host) or through site-host's proxy (for tenant domains), so without this every
// caller on the whole public delivery plane — analytics, form submissions, visitor login,
// chat — shares one partition keyed on the proxy's address: a single 600/60s bucket for all
// tenants, and no per-IP throttle on visitor login at all.
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
// Only reachable from inside the compose network, so every upstream is a trusted proxy.
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaders);

app.UseDcmsSecurityHeaders();
app.UseRateLimiter();
app.UseCors();
app.UseDcmsAudit();
app.UseMultiTenant();
app.UseAuthentication();
app.UseAuthorization();
app.MapDcmsDefaultEndpoints();
app.MapDcmsPlugins();
app.MapPluginConfig();
app.MapContentDelivery();
app.MapTagDelivery();
app.MapMediaDelivery();
app.MapOpenApi();
app.MapGet("/", () => Results.Ok(new { service = "content-api" }));
app.Run();

public partial class Program;
