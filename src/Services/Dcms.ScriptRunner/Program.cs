using Dcms.ScriptRunner;
using Dcms.Shared.Audit;
using Dcms.Shared.Hosting;

// Runs Dynamic Apps flow scripts (ADR 0025) and builds and runs Functions (ADR 0029), and
// nothing else. It holds no credentials — no
// Vault, database, broker or storage — and sits on a network without egress that only the
// flow executors and function brokers share, so code that escaped its Deno sandbox would find
// nothing to take and nowhere to send it. That network membership is also the whole of its
// access control. A function reaches DCMS only through its caller, over the session socket.
var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("script-runner", AuditProfile.Consumer);
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 8 * 1024 * 1024);
builder.Services.Configure<ScriptRunnerOptions>(builder.Configuration.GetSection("ScriptRunner"));
builder.Services.AddSingleton<DenoSandbox>();
builder.Services.AddSingleton<FunctionSession>();
builder.Services.AddSingleton<FunctionBuilder>();

var app = builder.Build();
app.UseDcmsProblemDetails();

app.MapDcmsDefaultEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "script-runner" }));
app.MapPost("/run", async (RunRequest request, DenoSandbox sandbox, CancellationToken ct) => Results.Ok(await sandbox.RunAsync(request, ct)));
app.MapPost("/build", async (BuildRequest request, FunctionBuilder builder, CancellationToken ct) => Results.Ok(await builder.BuildAsync(request, ct)));
app.UseWebSockets();
app.Map("/session", async (HttpContext http, FunctionSession session) =>
{
    if (!http.WebSockets.IsWebSocketRequest)
    {
        return Results.BadRequest();
    }
    using var socket = await http.WebSockets.AcceptWebSocketAsync();
    await session.RunAsync(socket, http.RequestAborted);
    return Results.Empty;
});
app.Run();

public partial class Program;
