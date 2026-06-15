using Dcms.MediaWorker;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Hosting;
using Dcms.Shared.Media;
using Dcms.Shared.Messaging;
using Dcms.Shared.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("media-worker");
builder.Services.AddDcmsMessaging(builder.Configuration);
builder.Services.AddDcmsObjectStorage(builder.Configuration);

// Reads/writes the media schema with no ambient tenant (scoped per job).
builder.Services.AddDcmsMediaData(builder.Configuration);
builder.Services.AddNullTenantContext();
builder.Services.AddSingleton<WebpLadderGenerator>();
builder.Services.AddSingleton<VideoTranscoder>();
builder.Services.AddSingleton<AudioTranscoder>();
builder.Services.AddHostedService<ImageProcessingConsumer>();
builder.Services.AddHostedService<VideoProcessingConsumer>();
builder.Services.AddHostedService<AudioProcessingConsumer>();

var app = builder.Build();
app.MapDcmsDefaultEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "media-worker" }));
app.Run();

public partial class Program;
