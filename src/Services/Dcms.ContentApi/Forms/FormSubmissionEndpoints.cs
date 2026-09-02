using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using System.Text.Json;
using Dcms.Plugins.Forms;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Dcms.Shared.Messaging.Email;
using Dcms.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Dcms.ContentApi.Forms;

/// <summary>
/// Visitor form submissions: POST /api/{slug}/forms/{formName} (the Forms plugin).
/// The plugin SDK does not mount custom plugin routes, so — like the analytics
/// beacon and visitor auth — the endpoint lives here and validates against the
/// form declared on the resolved plugin instance's config.
/// </summary>
public static class FormSubmissionEndpoints
{
    /// <summary>
    /// CORS policy for submissions from an externally hosted tenant site. Shares
    /// the analytics beacon's any-origin/no-credentials shape: a submission
    /// carries no cookie or token, so there is nothing for a hostile origin to
    /// ride on. Registered in content-api's Program.cs.
    /// </summary>
    public const string SubmitCorsPolicy = "forms-submit";

    public static IEndpointRouteBuilder MapFormSubmissions(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/{slug}/forms/{formName}", async (
            string slug, string formName, JsonElement body, HttpContext http,
            ITenantContext tenant, CmsDbContext cms, FormsDbContext forms,
            IEmailQueue email, IEventPublisher events, DcmsMetrics metrics,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.NotFound();
            }

            var instance = await cms.PluginInstances.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Slug == slug && p.PluginId == FormsPlugin.PluginId && p.Enabled, ct);
            if (instance is null)
            {
                return Results.NotFound();
            }

            using var config = JsonDocument.Parse(string.IsNullOrWhiteSpace(instance.ConfigJson) ? "{}" : instance.ConfigJson);
            var definition = FormsPlugin.ReadForms(config)
                .FirstOrDefault(f => string.Equals(f.Name, formName, StringComparison.Ordinal));
            if (definition is null)
            {
                return Results.NotFound();
            }

            if (body.ValueKind != JsonValueKind.Object)
            {
                return Results.BadRequest(new { error = "Expected a JSON object." });
            }

            if (Validate(definition, body) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            // Persist only the declared fields, so an inflated body can't be used
            // to store arbitrary data against the tenant.
            var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var field in definition.Fields)
            {
                if (body.TryGetProperty(field.Name, out var value) && value.ValueKind != JsonValueKind.Null)
                {
                    payload[field.Name] = value;
                }
            }

            var submission = new FormSubmission
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PluginInstanceId = instance.Id,
                FormName = definition.Name,
                DataJson = JsonSerializer.Serialize(payload),
                UserAgent = Truncate(http.Request.Headers.UserAgent.ToString(), 512),
            };
            forms.Submissions.Add(submission);
            await forms.SaveChangesAsync(ct);

            // Counted whether or not a notification is configured, and before the notification
            // is attempted: the submission is the thing that happened, and the mail is an
            // optional consequence of it that must not be able to change the count.
            metrics.FormSubmission(tenantId);

            // Queue the notification after the write, so a mail problem can never cost
            // a submission: email-worker owns delivery and retries from here on. A
            // failed enqueue (NATS down) is logged and swallowed for the same reason —
            // the submission is already safe in the database and visible in the admin.
            // Preview submissions are test data and deliberately stay silent.
            if (!submission.IsSandbox && definition.Notify is { } notify)
            {
                try
                {
                    var notification = BuildNotification(instance.Name, definition, notify, payload, submission);
                    await email.EnqueueAsync(notification.ToEmail(), ct);
                }
                catch (Exception ex)
                {
                    loggerFactory.CreateLogger(typeof(FormSubmissionEndpoints)).LogWarning(
                        ex, "Failed to queue notification for submission {SubmissionId}.", submission.Id);
                }
            }

            // In-app notification for the tenant's admins, independent of the email above:
            // email notification is opt-in per form, but a submission is always worth showing
            // in the bell. content-api cannot write the notifications schema (admin-api owns
            // it), so this goes over the bus and admin-api's ingest consumer does the insert.
            // Sandbox submissions stay silent for the same reason they send no mail.
            if (!submission.IsSandbox)
            {
                try
                {
                    await events.PublishAsync(Subjects.NotifyRaise, new NotificationRaiseRequested(
                        EventId: Guid.NewGuid(),
                        OccurredAt: DateTimeOffset.UtcNow,
                        TenantId: tenantId,
                        Kind: "form.submitted",
                        Severity: "Info",
                        // Whoever may read submissions is exactly who should hear about one.
                        RequiredPermission: PlatformPermissions.ContentRead,
                        // Underscored, not dotted: i18next treats "." as its key separator, so
                        // a dotted kind would be resolved as a nested lookup and never match.
                        TitleKey: "notifications.kinds.form_submitted.title",
                        BodyKey: "notifications.kinds.form_submitted.body",
                        ParamsJson: JsonSerializer.Serialize(new { form = definition.Title }),
                        // The submission id, so a JetStream redelivery collapses onto one row.
                        DedupeKey: $"form.submitted:{submission.Id:N}",
                        LinkPath: "/forms",
                        ResourceType: "form_submission",
                        ResourceId: submission.Id), ct);
                }
                catch (Exception ex)
                {
                    loggerFactory.CreateLogger(typeof(FormSubmissionEndpoints)).LogWarning(
                        ex, "Failed to raise notification for submission {SubmissionId}.", submission.Id);
                }
            }

            return Results.Accepted(value: new
            {
                submissionId = submission.Id,
                message = definition.SuccessMessage ?? "Thanks — your submission has been received.",
            });
        }).RequireCors(SubmitCorsPolicy).WithAudit(AuditActions.FormSubmitted, "form_submission");

        return app;
    }

    /// <summary>
    /// Collects the submission into the notification that gets rendered and queued.
    /// Every declared field appears, in form order, so a notification reads the same
    /// way whether or not the visitor filled in the optional ones.
    /// </summary>
    private static FormNotificationMessage BuildNotification(
        string instanceName,
        FormDefinition definition,
        FormNotificationDefinition notify,
        IReadOnlyDictionary<string, JsonElement> payload,
        FormSubmission submission)
    {
        var title = definition.Title ?? definition.Name;
        var values = definition.Fields
            .Select(f => (
                Label: f.Label ?? f.Name,
                Value: payload.TryGetValue(f.Name, out var value) ? Display(value) : "—"))
            .ToList();

        string? replyTo = null;
        if (!string.IsNullOrWhiteSpace(notify.ReplyToField) &&
            payload.TryGetValue(notify.ReplyToField, out var reply) &&
            reply.ValueKind == JsonValueKind.String)
        {
            replyTo = reply.GetString();
        }

        return new FormNotificationMessage(
            Recipients: notify.Recipients,
            Subject: string.IsNullOrWhiteSpace(notify.Subject) ? $"New {title} submission" : notify.Subject,
            FormTitle: title,
            InstanceName: instanceName,
            SubmissionId: submission.Id,
            TenantId: submission.TenantId,
            SubmittedAt: submission.SubmittedAt,
            Values: values,
            ReplyTo: replyTo);
    }

    private static string Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        _ => value.GetRawText(),
    };

    /// <summary>Returns an error message, or null when the body satisfies the form.</summary>
    private static string? Validate(FormDefinition definition, JsonElement body)
    {
        foreach (var field in definition.Fields)
        {
            var present = body.TryGetProperty(field.Name, out var value) && value.ValueKind != JsonValueKind.Null;

            if (!present)
            {
                if (field.Required)
                {
                    return $"'{field.Label ?? field.Name}' is required.";
                }
                continue;
            }

            switch (field.Type)
            {
                case "checkbox" when value.ValueKind is not (JsonValueKind.True or JsonValueKind.False):
                    return $"'{field.Label ?? field.Name}' must be true or false.";

                case "number" when value.ValueKind != JsonValueKind.Number:
                    return $"'{field.Label ?? field.Name}' must be a number.";

                case "checkbox":
                case "number":
                    break;

                default:
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        return $"'{field.Label ?? field.Name}' must be text.";
                    }
                    var text = value.GetString() ?? string.Empty;
                    if (field.Required && string.IsNullOrWhiteSpace(text))
                    {
                        return $"'{field.Label ?? field.Name}' is required.";
                    }
                    if (text.Length > field.MaxLength)
                    {
                        return $"'{field.Label ?? field.Name}' must be {field.MaxLength} characters or fewer.";
                    }
                    if (field.Type == "email" && present && !string.IsNullOrWhiteSpace(text) && !LooksLikeEmail(text))
                    {
                        return $"'{field.Label ?? field.Name}' must be a valid email address.";
                    }
                    if (field.Type == "date" && !string.IsNullOrWhiteSpace(text) &&
                        !DateOnly.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out _))
                    {
                        return $"'{field.Label ?? field.Name}' must be a date (YYYY-MM-DD).";
                    }
                    break;
            }
        }

        return null;
    }

    // Deliberately loose: real deliverability is only ever proven by sending mail,
    // so this rejects the obviously-malformed and nothing more.
    private static bool LooksLikeEmail(string value)
    {
        var at = value.IndexOf('@');
        return at > 0 && at < value.Length - 1 && value.IndexOf('@', at + 1) < 0 && !value.Contains(' ');
    }

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
