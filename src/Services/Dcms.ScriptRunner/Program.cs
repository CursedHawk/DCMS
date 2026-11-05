using Dcms.ScriptRunner;
using Dcms.Shared.Audit;
using Dcms.Shared.Hosting;

// Builds and runs Functions (ADR 0029), and nothing else. It holds no credentials — no
// Vault, database, broker or storage — and sits on a network without egress that only the
// function brokers share, so code that escaped its Deno sandbox would find
// nothing to take and nowhere to send it. That network membership is also the whole of its
// access control. A function reaches DCMS only through its caller, over the session socket.
var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("script-runner", AuditProfile.Consumer);
// A build may carry its packages' archives (ADR 0031): up to 60 MB, base64 in JSON. Only admin-api calls this.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 100 * 1024 * 1024);
builder.Services.Configure<ScriptRunnerOptions>(builder.Configuration.GetSection("ScriptRunner"));
builder.Services.AddSingleton<FunctionSession>();
builder.Services.AddSingleton<FunctionBuilder>();

var app = builder.Build();
app.UseDcmsProblemDetails();

app.MapDcmsDefaultEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "script-runner" }));
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
