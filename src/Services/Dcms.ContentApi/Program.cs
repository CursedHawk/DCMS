using Dcms.ContentApi.Branding;
using Dcms.ContentApi.Chat;
using Dcms.ContentApi.Delivery;
using Dcms.ContentApi.Forms;
using Dcms.ContentApi.Plugins;
using Dcms.PluginSdk.Runtime;
using Dcms.Plugins.All;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Caching;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Hosting;
using Dcms.ContentApi.Visitors;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Search;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.Visitors;
using Dcms.Shared.Messaging;
using Dcms.Shared.Messaging.Email;
using Dcms.Shared.Security;
using Dcms.Shared.Storage;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("content-api");
builder.Services.AddDcmsMessaging(builder.Configuration);
builder.Services.AddDcmsCaching(builder.Configuration);
builder.Services.AddDcmsObjectStorage(builder.Configuration);

// Tenant resolution: header in Phase 4 (host/domain routing arrives with
// site-host in Phase 8). Reads the tenancy + CMS schemas owned by admin-api.
builder.Services.AddDcmsTenancyData(builder.Configuration);
builder.Services.AddDcmsAuditData(builder.Configuration);
builder.Services.AddDcmsCmsData(builder.Configuration);
builder.Services.AddDcmsMediaData(builder.Configuration);
builder.Services.AddDcmsFormsData(builder.Configuration);

// Optional per-form email notifications. Rendered here, delivered by email-worker
// off the EMAIL work queue — content-api never touches SMTP.
builder.Services.AddDcmsEmailQueue();
// Country for analytics comes from the edge; swap this registration for a GeoIP
// database implementation if the deployment has no country-stamping proxy.
builder.Services.AddSingleton<Dcms.ContentApi.Delivery.IGeoIpResolver, Dcms.ContentApi.Delivery.HeaderGeoIpResolver>();
builder.Services.AddDcmsSearchData(builder.Configuration);
builder.Services.AddDcmsVisitorsData(builder.Configuration);
builder.Services.AddDcmsChatData(builder.Configuration);
builder.Services.Configure<VisitorTokenOptions>(builder.Configuration.GetSection(VisitorTokenOptions.SectionName));

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
builder.Services.AddSingleton(sp =>
    new VisitorTokenService(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<VisitorTokenOptions>>().Value));
builder.Services.AddDcmsTenantResolutionByHeader();

// Preview sandbox: the admin-api preview proxy sets X-Dcms-Sandbox so a site
// preview's writes (forms, visitors, chat) land in the per-tenant sandbox space.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Dcms.Shared.Kernel.Abstractions.ISandboxContext, Dcms.ContentApi.HeaderSandboxContext>();

// Platform-user authentication for the chat hub's agent role. Visitors connect
// anonymously; only agents present a platform JWT (carried in the access_token
// query string because WebSockets can't set custom headers).
builder.Services.AddDcmsResourceAuthentication(builder.Configuration);
builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
{
    jwt.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var token = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(token) && path.StartsWithSegments("/hub"))
            {
                context.Token = token;
            }
            return Task.CompletedTask;
        },
    };
});

// Live chat: SignalR with a Redis backplane so message fan-out crosses replicas.
var signalR = builder.Services.AddSignalR();
var redisConnection = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    signalR.AddStackExchangeRedis(redisConnection + ",abortConnect=false",
        options => options.Configuration.ChannelPrefix = RedisChannel.Literal("dcms-chat"));
}

builder.Services.AddDcmsRateLimiting(builder.Configuration);

// CORS for the admin SPA's cross-origin chat-hub connection (SignalR with a
// credentialed token requires explicit origins + AllowCredentials).
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173", "http://localhost:5000"];
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
    // Anonymous analytics beacon: any origin may POST events (no credentials),
    // so externally hosted sites can use the documented collect API.
    options.AddPolicy(AnalyticsIngestEndpoints.CollectCorsPolicy, policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .WithMethods("POST"));
    // Form submissions carry no cookie or token, so the same any-origin,
    // no-credentials shape applies: externally hosted tenant sites can post.
    options.AddPolicy(FormSubmissionEndpoints.SubmitCorsPolicy, policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .WithMethods("POST"));
    // Public branding read: any origin may GET (no credentials), so externally
    // hosted tenant sites can fetch their branding.
    options.AddPolicy(BrandingEndpoints.ReadCorsPolicy, policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .WithMethods("GET"));
});

// Outbound client-credentials token provider + ai-gateway client, used by the AI
// Chatbot to generate visitor replies. Reuses the shared dcms.ai service client.
builder.Services.Configure<ServiceClientOptions>(builder.Configuration.GetSection(ServiceClientOptions.SectionName));
builder.Services.AddHttpClient<IServiceTokenProvider, ServiceTokenClient>();
builder.Services.AddHttpClient("ai-gateway", (sp, client) =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["Services:AiGateway"] ?? "http://localhost:5007";
    client.BaseAddress = new Uri(baseUrl);
});
builder.Services.AddSingleton<ChatBotResponder>();

builder.Services.AddDcmsPlugins(plugins => plugins.AddAll());
builder.Services.AddScoped<PublishedContentReader>();
builder.Services.AddScoped<IMediaResolver, Dcms.ContentApi.Delivery.MediaResolver>();
builder.Services.AddHostedService<ContentCacheInvalidator>();
builder.Services.AddHostedService<SearchIndexer>();

var app = builder.Build();
// First in the pipeline, so an exception anywhere below it becomes a ProblemDetails
// carrying the trace id instead of a bare Kestrel 500 with no body and nothing to quote.
app.UseDcmsProblemDetails();

// Before the rate limiter, which partitions on Connection.RemoteIpAddress, and before the
// audit middleware, which records it. content-api is reached only through Caddy (for /hub on
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
app.MapSearchDelivery();
app.MapAnalyticsIngest();
app.MapFormSubmissions();
app.MapBranding();
app.MapPluginConfig();
app.MapVisitorAuth();
app.MapChatDelivery();
app.MapContentDelivery();
app.MapTagDelivery();
app.MapMediaDelivery();
app.MapOpenApi();
app.MapHub<ChatHub>("/hub/chat")
    .AuditExempt("SignalR transport endpoint, not an action. Chat messages are recorded by the "
               + "hub methods that write them, where the conversation and author are known.");
app.MapGet("/", () => Results.Ok(new { service = "content-api" }));
app.Run();

public partial class Program;
