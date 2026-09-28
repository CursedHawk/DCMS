using System.Text.Json;
using Dcms.Plugins.VisitorAuth.Contracts;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Security;
using Dcms.Shared.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.Forms;

/// <summary>
/// Visitor form submissions: POST /api/{slug}/forms/{formName}, mounted by the plugin runtime
/// for an enabled Forms instance only. Validates against the form declared in the instance
/// config, stores the declared fields, then — through platform contracts, never directly —
/// emails the configured recipients, notifies the admins and publishes <c>form.submitted</c>.
/// </summary>
internal static class FormSubmissionEndpoints
{
    public static void Map(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapPost("/forms/{formName}", async (
            string formName, JsonElement body, HttpContext http, IPluginContext context,
            FormsDbContext forms, DcmsMetrics metrics, ILogger<FormsPlugin> logger, CancellationToken ct) =>
        {
            var instance = context.Instance!;
            var definition = FormsPlugin.ReadForms(instance.Config)
                .FirstOrDefault(f => string.Equals(f.Name, formName, StringComparison.Ordinal));
            if (definition is null)
            {
                return Results.NotFound();
            }
            if (body.ValueKind != JsonValueKind.Object)
            {
                return Results.BadRequest(new { error = "Expected a JSON object." });
            }

            // Optional: without VisitorAuth, or for an anonymous visitor, there is simply nobody.
            var visitor = context.Contracts.TryGet<IVisitorIdentity>() is { } identity
                ? (await identity.GetCurrentAsync(ct)).Visitor
                : null;

            var submitted = body.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
            if (visitor is not null)
            {
                Prefill(definition, submitted, visitor);
            }

            if (Validate(definition, submitted) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            // Persist only the declared fields, so an inflated body can't be used to store
            // arbitrary data against the tenant.
            var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var field in definition.Fields)
            {
                if (submitted.TryGetValue(field.Name, out var value) && value.ValueKind != JsonValueKind.Null)
                {
                    payload[field.Name] = value;
                }
            }

            var submission = new FormSubmission
            {
                Id = Guid.NewGuid(),
                TenantId = context.TenantId,
                PluginInstanceId = instance.InstanceId,
                FormName = definition.Name,
                DataJson = JsonSerializer.Serialize(payload),
                UserAgent = Truncate(http.Request.Headers.UserAgent.ToString(), 512),
                VisitorId = visitor?.Id,
            };
            forms.Submissions.Add(submission);
            await forms.SaveChangesAsync(ct);

            // Counted whether or not a notification is configured, and before one is attempted:
            // the submission is the thing that happened; the mail is an optional consequence.
            metrics.FormSubmission(context.TenantId);

            // Everything below is after the write and best effort: the submission is already
            // safe and visible in the admin, so a mail or bus problem must never cost it. Preview
            // (sandbox) submissions are test data and stay silent.
            if (!submission.IsSandbox)
            {
                await AfterSubmitAsync(context, instance, definition, payload, submission, logger, ct);
            }

            return Results.Accepted(value: new
            {
                submissionId = submission.Id,
                message = definition.SuccessMessage ?? "Thanks — your submission has been received.",
            });
        }).RequireCors(FormsPlugin.SubmitCorsPolicy)
          .PermissionExempt("Anonymous by design: any visitor may submit a declared form; only declared fields are stored.")
          .WithAudit(AuditActions.FormSubmitted, "form_submission");
    }

    private static async Task AfterSubmitAsync(
        IPluginContext context, PluginInstanceContext instance, FormDefinition definition,
        IReadOnlyDictionary<string, JsonElement> payload, FormSubmission submission, ILogger logger, CancellationToken ct)
    {
        if (definition.Notify is { } notify)
        {
            await BestEffort(logger, "queue the notification email", submission.Id, () =>
                context.Contracts.Get<IPluginEmail>().SendAsync(
                    BuildNotification(instance.Name, definition, notify, payload, submission).ToEmail(), ct));
        }

        // In the bell for everyone who may read submissions, whether or not email is on.
        await BestEffort(logger, "raise the admin notification", submission.Id, () =>
            context.Contracts.Get<IPluginNotifications>().RaiseAsync(new NotificationRaise(
                Title: "New form submission",
                Body: $"Someone submitted the {definition.Title ?? definition.Name} form.",
                RequiredPermission: FormsPlugin.SubmissionsReadPermission,
                // The submission id, so a redelivery collapses onto one row.
                DedupeKey: $"submitted:{submission.Id:N}",
                LinkPath: "/forms",
                Kind: "submitted",
                Params: new Dictionary<string, string> { ["form"] = definition.Title ?? definition.Name },
                ResourceType: "form_submission",
                ResourceId: submission.Id), ct));

        await BestEffort(logger, "publish form.submitted", submission.Id, () =>
            context.PublishAsync(new FormSubmitted(submission.Id, instance.InstanceId, definition.Name, submission.VisitorId), ct));
    }

    private static async Task BestEffort(ILogger logger, string what, Guid submissionId, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to {What} for submission {SubmissionId}.", what, submissionId);
        }
    }

    /// <summary>
    /// Fills fields the visitor left out from their profile (<c>prefill: "visitor.email"</c>,
    /// <c>"visitor.displayName"</c> or <c>"visitor.{attribute}"</c>). Only what VisitorAuth shares
    /// with plugins is available; a value the visitor did send always wins.
    /// </summary>
    private static void Prefill(FormDefinition definition, Dictionary<string, JsonElement> submitted, VisitorProfile visitor)
    {
        foreach (var field in definition.Fields)
        {
            if (field.Prefill is not { } source || !source.StartsWith("visitor.", StringComparison.Ordinal)
                || (submitted.TryGetValue(field.Name, out var sent) && sent.ValueKind != JsonValueKind.Null))
            {
                continue;
            }
            var key = source["visitor.".Length..];
            JsonElement? value = key switch
            {
                "email" => JsonSerializer.SerializeToElement(visitor.Email),
                "displayName" when visitor.DisplayName is { } name => JsonSerializer.SerializeToElement(name),
                _ when visitor.Attributes.TryGetValue(key, out var attribute) => attribute,
                _ => null,
            };
            if (value is { } v)
            {
                submitted[field.Name] = v;
            }
        }
    }

    /// <summary>
    /// Collects the submission into the notification that gets rendered and queued. Every
    /// declared field appears, in form order, so a notification reads the same way whether or
    /// not the visitor filled in the optional ones.
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
    private static string? Validate(FormDefinition definition, IReadOnlyDictionary<string, JsonElement> body)
    {
        foreach (var field in definition.Fields)
        {
            var present = body.TryGetValue(field.Name, out var value) && value.ValueKind != JsonValueKind.Null;

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
                    if (field.Type == "email" && !string.IsNullOrWhiteSpace(text) && !LooksLikeEmail(text))
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
