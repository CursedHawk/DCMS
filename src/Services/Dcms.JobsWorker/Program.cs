using Dcms.JobsWorker;
using Dcms.Shared.AdminPlane;
using Dcms.Shared.Hosting;

// The platform's background work, and nothing else (ADR 0036): job lanes, plugin workers, event
// consumers, outboxes and sweeps. It serves no routes but health -- nothing calls it; it drains
// queues and tables -- and it is stateless, so more throughput is more replicas: every piece
// below is a shared durable consumer, a SKIP LOCKED claim, a lease or an advisory-locked pass.
//
// It shares admin-api's composition (data, messaging, the SignalR backplane it pushes through,
// plugins, the domain services) through AddDcmsAdminPlane, so the two cannot drift.
var builder = WebApplication.CreateBuilder(args);
const string serviceName = "jobs-worker";
builder.AddDcmsServiceDefaults(serviceName);
builder.AddDcmsAdminPlane(serviceName, runsBackgroundWork: true);
builder.Services.AddJobsWorker(builder.Configuration);

var app = builder.Build();
app.UseDcmsProblemDetails();
app.MapDcmsDefaultEndpoints();
app.MapGet("/", () => Results.Ok(new { service = serviceName }));
app.Run();

public partial class Program;
