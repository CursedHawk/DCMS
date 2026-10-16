using Dcms.ScriptRunner;
using Dcms.Shared.Audit;
using Dcms.Shared.Hosting;

// Runs Dynamic Apps flow scripts (ADR 0025), and nothing else. It holds no credentials — no
// Vault, database, broker or storage — and sits on a network without egress that only the
// flow executors share, so a script that escaped its Deno sandbox would find nothing to take
// and nowhere to send it. That network membership is also the whole of its access control.
var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("script-runner", AuditProfile.Consumer);
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 4 * 1024 * 1024);
builder.Services.Configure<ScriptRunnerOptions>(builder.Configuration.GetSection("ScriptRunner"));
builder.Services.AddSingleton<DenoSandbox>();

var app = builder.Build();
app.UseDcmsProblemDetails();

app.MapDcmsDefaultEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "script-runner" }));
app.MapPost("/run", async (RunRequest request, DenoSandbox sandbox, CancellationToken ct) => Results.Ok(await sandbox.RunAsync(request, ct)));
app.Run();

public partial class Program;
