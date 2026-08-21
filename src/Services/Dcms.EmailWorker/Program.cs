using Dcms.Shared.Audit;
using Dcms.EmailWorker;
using Dcms.Shared.Hosting;
using Dcms.Shared.Messaging;
using Dcms.Shared.Messaging.Email;

// The only process in the platform that speaks SMTP. Everything else publishes to
// the EMAIL work queue; this drains it, retries, and dead-letters. Keeping delivery
// here means relay credentials live in exactly one container, and a slow or broken
// relay never shows up as a slow HTTP request to a user.
var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("email-worker", AuditProfile.Consumer);
builder.Services.AddDcmsMessaging(builder.Configuration);
// No database connection at all, so the audit schema is out of reach: records ride JetStream
// and admin-api's writer appends them to the chain.
builder.Services.AddDcmsAuditOverNats();
builder.Services.AddDcmsEmailSender(builder.Configuration);
builder.Services.AddHostedService<EmailSendConsumer>();

var app = builder.Build();
app.MapDcmsDefaultEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "email-worker" }));
app.Run();

public partial class Program;
