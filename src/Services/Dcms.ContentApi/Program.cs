using Dcms.ContentApi.Chat;
using Dcms.ContentApi.Delivery;
using Dcms.ContentApi.Forms;
using Dcms.ContentApi.Plugins;
using Dcms.PluginSdk.Runtime;
using Dcms.Plugins.All;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Caching;
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
using Dcms.Shared.Security;
using Dcms.Shared.Storage;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("content-api");
builder.Services.AddDcmsMessaging(builder.Configuration);
builder.Services.AddDcmsCaching(builder.Configuration);
builder.Services.AddDcmsObjectStorage(builder.Configuration);

// Tenant resolution: header in Phase 4 (host/domain routing arrives with
// site-host in Phase 8). Reads the tenancy + CMS schemas owned by admin-api.
builder.Services.AddDcmsTenancyData(builder.Configuration);
builder.Services.AddDcmsCmsData(builder.Configuration);
builder.Services.AddDcmsMediaData(builder.Configuration);
builder.Services.AddDcmsFormsData(builder.Configuration);
builder.Services.AddDcmsSearchData(builder.Configuration);
builder.Services.AddDcmsVisitorsData(builder.Configuration);
builder.Services.AddDcmsChatData(builder.Configuration);
builder.Services.Configure<VisitorTokenOptions>(builder.Configuration.GetSection(VisitorTokenOptions.SectionName));
builder.Services.AddSingleton(sp =>
    new VisitorTokenService(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<VisitorTokenOptions>>().Value));
builder.Services.AddDcmsTenantResolutionByHeader();

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
});

builder.Services.AddDcmsPlugins(plugins => plugins.AddAll());
builder.Services.AddScoped<PublishedContentReader>();
builder.Services.AddScoped<IMediaResolver, Dcms.ContentApi.Delivery.MediaResolver>();
builder.Services.AddHostedService<ContentCacheInvalidator>();
builder.Services.AddHostedService<SearchIndexer>();

var app = builder.Build();
app.UseDcmsSecurityHeaders();
app.UseRateLimiter();
app.UseCors();
app.UseMultiTenant();
app.UseAuthentication();
app.UseAuthorization();
app.MapDcmsDefaultEndpoints();
app.MapDcmsPlugins();
app.MapSearchDelivery();
app.MapAnalyticsIngest();
app.MapFormSubmissions();
app.MapPluginConfig();
app.MapVisitorAuth();
app.MapChatDelivery();
app.MapContentDelivery();
app.MapMediaDelivery();
app.MapOpenApi();
app.MapHub<ChatHub>("/hub/chat");
app.MapGet("/", () => Results.Ok(new { service = "content-api" }));
app.Run();

public partial class Program;
