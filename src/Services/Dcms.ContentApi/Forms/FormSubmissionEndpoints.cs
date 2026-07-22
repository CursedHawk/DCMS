using System.Text.Json;
using Dcms.Plugins.Forms;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Kernel.Abstractions;
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
            ITenantContext tenant, CmsDbContext cms, FormsDbContext forms, CancellationToken ct) =>
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

            return Results.Accepted(value: new
            {
                submissionId = submission.Id,
                message = definition.SuccessMessage ?? "Thanks — your submission has been received.",
            });
        }).RequireCors(SubmitCorsPolicy);

        return app;
    }

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
