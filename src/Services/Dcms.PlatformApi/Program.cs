using Dcms.PlatformApi.Authz;
using Dcms.PlatformApi.Delegation;
using Dcms.PlatformApi.Observability;
using Dcms.PlatformApi.Purge;
using Dcms.PlatformApi.Realtime;
using Dcms.PlatformApi.Reporting;
using Dcms.PlatformApi.Stores;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Caching;
using Dcms.Shared.Data.Platform;
using Dcms.Shared.Hosting;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;

var builder = WebApplication.CreateBuilder(args);

// Vault config provider, Serilog (console + OTLP), OpenTelemetry, health endpoints and the
// ProblemDetails handler that puts a trace id on every failure. The OTLP half is gated on
// OTEL_EXPORTER_OTLP_ENDPOINT: a service that skips the *otel-env compose anchor builds its
// pipelines and exports nothing, which is invisible until a dashboard panel reads "No data".
builder.AddDcmsServiceDefaults("platform-api");

builder.Services.AddDcmsCaching(builder.Configuration);
builder.Services.AddDcmsMessaging(builder.Configuration);

// Audit records ride JetStream instead of being written straight to Postgres, exactly as
// email-worker and site-builder do — and here for the same reason those do it. The
// least-privilege dcms_platform role below has no grant on the `audit` schema, so this
// service could not append to the hash chain even if it wanted to; admin-api's writer owns
// that. The consequence is worth stating plainly, because this service holds the platform's
// delete buttons: a purge is audited by publishing, and a purge whose publish fails must fail
// with it rather than proceed unrecorded.
builder.Services.AddDcmsAuditOverNats();
builder.Services.AddDcmsResourceAuthentication(builder.Configuration);

// The console's own table: which global role holds which platform permission. This is the
// ONLY schema platform-api writes, and with `obs` it is the only one it can read — the
// connection is the least-privilege dcms_platform role (infra/postgres/init/04-platform-role.sh),
// which has no grant on identity, tenancy or any tenant schema. Users are reached over HTTP
// from identity, tenants over HTTP from admin-api; each schema stays behind its owner.
builder.Services.AddDcmsPlatformData(builder.Configuration);

// Platform-console authorization. There is deliberately no AddDcmsPermissionAuthorization
// here and no tenant resolution anywhere in this service: nothing it serves is scoped to a
// tenant, so there is no ambient tenant to get wrong.
builder.Services.AddDcmsPlatformPermissionAuthorization();
builder.Services.AddScoped<PlatformPermissionResolver>();
builder.Services.AddScoped<IPlatformPermissionResolver>(sp =>
    sp.GetRequiredService<PlatformPermissionResolver>());
builder.Services.AddHostedService<PlatformRoleSeeder>();

// Live updates for the console. The channel prefix MUST differ from admin-api's "dcms-notify"
// and content-api's "dcms-chat": all three share one Redis, and a shared prefix cross-delivers
// between hubs that know nothing about each other.
var consoleSignalR = builder.Services.AddSignalR();
var signalRRedis = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(signalRRedis))
{
    consoleSignalR.AddStackExchangeRedis(signalRRedis + ",abortConnect=false",
        options => options.Configuration.ChannelPrefix =
            StackExchange.Redis.RedisChannel.Literal("dcms-console"));
}
builder.Services.AddSingleton<IPlatformChangePublisher, PlatformChangePublisher>();
builder.Services.AddHostedService<PlatformLiveUpdates>();
builder.Services.AddHostedService<PlatformSampleBroadcaster>();

// Read-only access to the obs.* reporting views — the console's cross-tenant read model.
builder.Services.AddScoped<ObservabilityQuery>();

// The hop to admin-api for the four areas dcms_platform holds no grant on. The token is
// client-credentials on dcms.console; WHO the operator is rides in the propagation headers,
// because a service token alone would have admin-api record every suspension against this
// service instead of the person who asked for it.
builder.Services.Configure<ServiceClientOptions>(
    builder.Configuration.GetSection(ServiceClientOptions.SectionName));
builder.Services.AddHttpClient<IServiceTokenProvider, ServiceTokenClient>();
builder.Services.AddHttpClient<AdminApiProxy>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:AdminApi"] ?? "http://localhost:5002");
    // A console page, not a background job: a stalled admin-api should read as unreachable
    // rather than hold the request open.
    client.Timeout = TimeSpan.FromSeconds(20);
})
.AddAuditPropagation();

// The telemetry stores. All internal names on the compose network; none publishes a host port
// in production, so reaching them at all requires already being inside.
//
// Ten-second timeouts throughout: this console is opened when something is wrong, and a store
// that has stopped answering must show as unreachable rather than hang the page that would
// have told you so.
builder.Services.Configure<ObservabilityOptions>(
    builder.Configuration.GetSection(ObservabilityOptions.SectionName));

var observability = builder.Configuration.GetSection(ObservabilityOptions.SectionName)
    .Get<ObservabilityOptions>() ?? new ObservabilityOptions();

builder.Services.AddHttpClient<PrometheusClient>(client =>
{
    client.BaseAddress = new Uri(observability.PrometheusUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient<LokiClient>(client =>
{
    client.BaseAddress = new Uri(observability.LokiUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
    // auth_enabled is false on this Loki, which means every request belongs to the single
    // tenant named "fake". The delete API still wants the header, and omitting it fails in a
    // way that reads like a permissions problem rather than a missing header.
    client.DefaultRequestHeaders.Add("X-Scope-OrgID", "fake");
});

builder.Services.AddHttpClient<LogJanitorClient>(client =>
{
    // An empty base address is the disabled state; the endpoint checks LogJanitorEnabled before
    // ever resolving this client, so the placeholder is never dialled.
    client.BaseAddress = new Uri(
        string.IsNullOrWhiteSpace(observability.LogJanitorUrl)
            ? "http://log-janitor.disabled"
            : observability.LogJanitorUrl);
    client.Timeout = TimeSpan.FromSeconds(15);
});

// Refuse to start without a database credential, rather than starting and failing at the
// first query.
//
// The whole connection string lives in secret/dcms/platform-api, so a host with no AppRole
// gets no credential at all — VaultCredentials.FromEnvironment returns null, the provider is
// skipped, and configuration simply has no ConnectionStrings:Postgres. That service starts,
// answers /health/live (which by design runs no checks), passes the deploy's health gate, and
// is broken. A green deploy hiding a dead service is the failure this platform writes guards
// against everywhere else — identity refuses to boot without signing certificates, admin-api
// refuses a weak alert secret in Production — so this refuses too, and says exactly what to run.
if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Postgres")))
{
    throw new InvalidOperationException(
        "No ConnectionStrings:Postgres. platform-api reads it from Vault at "
        + "secret/dcms/platform-api and from nowhere else -- it is deliberately absent from "
        + "every compose file. On a new host: run `infra/vault/apply.sh` to create this "
        + "service's policy and AppRole, `infra/vault/apply.sh --seed` to generate the "
        + "credential, and put VAULT_ROLE_ID_PLATFORM_API / VAULT_SECRET_ID_PLATFORM_API in "
        + ".env (`apply.sh --print-role-ids` prints the role id).");
}

var app = builder.Build();

// Fails now, with the two settings named, rather than on the first request to touch auth --
// which on this platform is the compose healthcheck, and which reported an options stack trace
// instead of "your Authority is http and RequireHttpsMetadata is true".
app.Services.ValidateDcmsResourceAuthentication();

// First in the pipeline, so an exception anywhere below it becomes a ProblemDetails carrying
// the trace id instead of a bare Kestrel 500 with no body and nothing to quote.
app.UseDcmsProblemDetails();

app.UseDcmsAudit();
app.UseAuthentication();
app.UseAuthorization();

app.MapDcmsDefaultEndpoints();
app.MapPlatformAuthzEndpoints();
app.MapPlatformOverviewEndpoints();
app.MapPlatformStoreEndpoints();
app.MapPlatformPurgeEndpoints();
app.MapPlatformAuditEndpoints();
app.MapPlatformHealthEndpoints();
app.MapDelegatedConsoleEndpoints();
// Same /api/platform prefix the edge already routes here, so the WebSocket upgrade needs no
// route of its own. Push-only: see PlatformHub for why there is nothing to audit.
app.MapHub<PlatformHub>("/api/platform/hub/console")
    .AuditExempt("SignalR transport endpoint, not an action. It carries only resource-change "
               + "tags, and every refetch they cause is an ordinary audited request.");
app.MapGet("/", () => Results.Ok(new { service = "platform-api" }));

app.Run();

public partial class Program;
