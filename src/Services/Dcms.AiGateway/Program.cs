using Dcms.AiGateway;
using Dcms.AiGateway.Providers;
using Dcms.Shared.Caching;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Audit;
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
