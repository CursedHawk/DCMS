using Dcms.AiGateway;
using Dcms.AiGateway.Providers;
using Dcms.Shared.Caching;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Data.DataProtection;
using Dcms.Shared.Hosting;
using Dcms.Shared.Security;
using Dcms.Shared.Vault;

var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("ai-gateway");
builder.Services.AddDcmsCaching(builder.Configuration);
builder.Services.AddDcmsResourceAuthentication(builder.Configuration);

// Reads tenant AI settings; decrypts tenant keys via Vault Transit at call time.
builder.Services.AddDcmsAiData(builder.Configuration);
builder.Services.AddDcmsAuditData(builder.Configuration);

// Data Protection, persisted to Postgres and shared with every other service.
//
// ai-gateway sets no cookies and issues no antiforgery tokens, so the natural assumption is
// that it needs none of this. It does: the container logs "Storing keys in a directory that
// may not be persisted" and mints a key at every startup, which means something in the shared
// stack resolves the provider and the ring is real whether or not this service uses it. An
// unshared, unencrypted, per-container ring is not a neutral default -- it writes key material
// into the container filesystem and it diverges silently between replicas.
//
// The Transit client below is already registered for tenant provider keys, so wrapping the
// ring costs nothing extra here when DataProtection:ProtectWithTransit is turned on.
builder.Services.AddDcmsDataProtection(builder.Configuration);
builder.Services.AddDcmsVaultTransit();
builder.Services.AddScoped<AiProviderResolver>();
builder.Services.AddHttpClient("anthropic");

var app = builder.Build();
// First in the pipeline, so an exception anywhere below it becomes a ProblemDetails
// carrying the trace id instead of a bare Kestrel 500 with no body and nothing to quote.
app.UseDcmsProblemDetails();

app.UseDcmsAudit();
app.UseAuthentication();
app.UseAuthorization();

app.MapDcmsDefaultEndpoints();
app.MapChatEndpoints();
app.MapMessagesEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "ai-gateway" }));

// Protected internal ping (used by admin-api to verify the service-to-service flow).
app.MapGet("/internal/ping", () => Results.Ok(new { service = "ai-gateway", pong = true }))
    .RequireAuthorization();

app.Run();

public partial class Program;
