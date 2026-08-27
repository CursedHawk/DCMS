using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging.Email;
using Microsoft.Extensions.Options;

namespace Dcms.AdminApi.Observability;

/// <summary>
/// Receives Grafana's alert webhook and puts the notification on the EMAIL work queue.
///
/// <para><b>Why a webhook rather than Grafana's own SMTP.</b> Grafana can send mail directly,
/// and doing so would mean putting the relay credentials in a second container.
/// <c>docker-compose.vps.yml</c> is explicit that email-worker is the "sole holder of the SMTP
/// relay credentials" and that everything wanting to send publishes to the queue instead —
/// identity for password resets, content-api for form notifications. An alerting system is not
/// a good reason to be the exception, particularly given the relay in use rejects any envelope
/// sender that is not the authenticated account, so a second sender is a second thing to get
/// wrong.</para>
///
/// <para>It also means alert mail inherits everything the queue already does: retry on a
/// relay that is briefly down, one message per recipient so a bounce to one does not resend
/// to the others, and delivery off the caller's thread.</para>
/// </summary>
public static class AlertEndpoints
{
    public const string Route = "/api/internal/alerts";

    public static void MapAlertEndpoints(this WebApplication app)
    {
        app.MapPost(Route, async (
            HttpRequest request,
            IEmailQueue email,
            IOptions<AlertingOptions> options,
            IAuditRecorder audit,
            AuditScope scope,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("AlertWebhook");
            var settings = options.Value;

            // Same rule as the git webhook: never authenticate against an unconfigured secret.
            // An empty expected value that anything matches would make this route a way for
            // anyone reaching admin-api to send mail from the platform's own address.
            if (string.IsNullOrWhiteSpace(settings.WebhookSecret))
            {
                log.LogWarning("Alert webhook rejected: Alerting:WebhookSecret is not configured.");
                audit.Record(AuditActions.WebhookRejected)
                    .Platform()
                    .As(AuditCategory.Security, AuditSeverity.Warning)
                    .Denied("Alerting:WebhookSecret is not configured");
                return Results.Unauthorized();
            }

            // A bearer token, not an HMAC over the body — and the difference from the git
            // webhook next door is a limitation, not a preference. Grafana's webhook contact
            // point can attach an Authorization header and cannot sign a payload, so there is
            // no signature to verify. A bearer is replayable in a way an HMAC is not.
            //
            // What makes that acceptable here is that this route is unreachable from outside:
            // admin-api publishes no host port, and infra/caddy/Caddyfile routes /api/* on
            // admin.highgeek.eu to admin-api — so a public request for /api/internal/alerts
            // does arrive. It is therefore NOT internal by network alone, which is exactly why
            // the secret is checked in constant time and why Program.cs refuses to start in
            // Production with a short or well-known one.
            if (!IsAuthorized(request, settings.WebhookSecret))
            {
                audit.Record(AuditActions.WebhookRejected)
                    .Platform()
                    .As(AuditCategory.Security, AuditSeverity.Warning)
                    .Denied("alert webhook credential missing or incorrect");
                return Results.Unauthorized();
            }

            using var reader = new StreamReader(request.Body);
            var raw = await reader.ReadToEndAsync(ct);

            // The credential proves the post came from the configured Grafana. It says nothing
            // about a person, so the actor is the system, inferred — the same honesty the git
            // webhook applies to a self-asserted git author.
            scope.Actor = new AuditActor(
                ActorKind.System, null, "grafana", "Grafana alerting", AuditAttribution.Inferred);

            GrafanaAlertPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<GrafanaAlertPayload>(raw, JsonOptions);
            }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "Alert webhook payload could not be parsed.");
                return Results.BadRequest(new { error = "unparseable payload" });
            }

            if (payload?.Alerts is not { Count: > 0 })
            {
                return Results.Ok(new { ignored = "no alerts in payload" });
            }

            var recipients = settings.RecipientList();

            if (recipients.Length == 0)
            {
                // Not an error and not silent. An alerting system nobody configured a
                // recipient for is a real state to be in during setup, and the alert has
                // already been recorded below either way.
                log.LogWarning(
                    "Alert {Status} received with no Alerting__Recipients configured; recorded but not mailed.",
                    payload.Status);
            }

            foreach (var alert in payload.Alerts)
            {
                var name = alert.Labels.GetValueOrDefault("alertname", "unnamed alert");
                var severity = alert.Labels.GetValueOrDefault("severity", "unknown");
                var summary = alert.Annotations.GetValueOrDefault("summary", string.Empty);

                audit.Record(AuditActions.AlertNotified)
                    .Platform()
                    .As(AuditCategory.Security, SeverityFor(severity))
                    .For("alert", name, summary)
                    .With("status", alert.Status)
                    .With("severity", severity)
                    .With("startsAt", alert.StartsAt?.ToString("O"))
                    .With("recipients", recipients.Length);

                if (recipients.Length == 0)
                {
                    continue;
                }

                try
                {
                    await email.EnqueueAsync(
                        new EmailMessage(
                            recipients,
                            Subject(payload.Status, severity, name),
                            Body(alert, name, severity, summary),
                            Purpose: "alert",
                            // One mail per alert per firing, not one per Grafana retry. The
                            // fingerprint plus the status is exactly that identity, and the
                            // stream's duplicate window discards the rest.
                            DedupeKey: alert.Fingerprint is null ? null : $"alert:{alert.Fingerprint}:{alert.Status}"),
                        ct);
                }
                catch (Exception ex)
                {
                    // Mail is best-effort here. The record above is already written, and
                    // failing the webhook would make Grafana retry the whole batch — mailing
                    // the alerts that did succeed a second time.
                    log.LogError(ex, "Could not queue notification for alert {Alert}.", name);
                }
            }

            return Results.Ok(new { received = payload.Alerts.Count });
        })
        .AllowAnonymous()
        .WithAudit(AuditActions.AlertNotified, "alert", AuditCategory.Security);
    }

    /// <summary>
    /// Constant-time comparison of the bearer credential. <c>FixedTimeEquals</c> requires
    /// equal lengths, so the length is checked first — and comparing hashes rather than the
    /// raw bytes keeps that first check from leaking the secret's length.
    /// </summary>
    private static bool IsAuthorized(HttpRequest request, string expected)
    {
        var header = request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        const string scheme = "Bearer ";
        var presented = header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            ? header[scheme.Length..].Trim()
            : header.Trim();

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }

    private static AuditSeverity SeverityFor(string severity) => severity switch
    {
        "critical" => AuditSeverity.Critical,
        "warning" => AuditSeverity.Warning,
        _ => AuditSeverity.Info,
    };

    private static string Subject(string status, string severity, string name) =>
        status.Equals("resolved", StringComparison.OrdinalIgnoreCase)
            ? $"[DCMS resolved] {name}"
            : $"[DCMS {severity}] {name}";

    private static string Body(GrafanaAlert alert, string name, string severity, string summary)
    {
        var builder = new StringBuilder();
        builder.Append("<h2>").Append(Escape(name)).Append("</h2>");
        builder.Append("<p><strong>Status:</strong> ").Append(Escape(alert.Status)).Append("</p>");
        builder.Append("<p><strong>Severity:</strong> ").Append(Escape(severity)).Append("</p>");

        if (!string.IsNullOrWhiteSpace(summary))
        {
            builder.Append("<p>").Append(Escape(summary)).Append("</p>");
        }

        if (alert.Annotations.TryGetValue("description", out var description) && !string.IsNullOrWhiteSpace(description))
        {
            builder.Append("<p>").Append(Escape(description)).Append("</p>");
        }

        if (alert.StartsAt is { } startsAt)
        {
            builder.Append("<p><strong>Since:</strong> ").Append(Escape(startsAt.ToString("u"))).Append("</p>");
        }

        // Every label except the ones already shown. Labels are set by the rule files in
        // infra/observability/prometheus/rules, so this is bounded, operator-authored text.
        var rest = alert.Labels
            .Where(l => l.Key is not ("alertname" or "severity"))
            .OrderBy(l => l.Key, StringComparer.Ordinal)
            .ToArray();

        if (rest.Length > 0)
        {
            builder.Append("<ul>");
            foreach (var (key, value) in rest)
            {
                builder.Append("<li><strong>").Append(Escape(key)).Append(":</strong> ")
                    .Append(Escape(value)).Append("</li>");
            }
            builder.Append("</ul>");
        }

        if (!string.IsNullOrWhiteSpace(alert.SilenceUrl))
        {
            builder.Append("<p><a href=\"").Append(Escape(alert.SilenceUrl)).Append("\">Silence this alert</a></p>");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Alert label and annotation values reach this from rule files, but a few carry a
    /// templated value derived from a metric label, and metric labels are ultimately derived
    /// from things like route patterns. Escaping is cheap and the alternative is HTML
    /// injection into an operator's inbox.
    /// </summary>
    private static string Escape(string value) =>
        System.Net.WebUtility.HtmlEncode(value);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

public sealed class AlertingOptions
{
    /// <summary>
    /// Shared secret Grafana signs its payload with. Empty means the endpoint refuses
    /// everything, which is the correct default for a route that can send mail.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Where alert mail goes, as a comma- or semicolon-separated list.
    ///
    /// <para>A single string rather than a bound array, because the value's real home is an
    /// <c>.env</c> file on the deployment host — and the array form
    /// (<c>Alerting__Recipients__0</c>, <c>__1</c>, …) is the sort of thing that gets one
    /// index wrong and silently drops a recipient. A configuration file can still set it as
    /// one string.</para>
    /// </summary>
    public string Recipients { get; set; } = string.Empty;

    public string[] RecipientList() =>
        Recipients.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

internal sealed record GrafanaAlertPayload(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("alerts")] IReadOnlyList<GrafanaAlert>? Alerts);

internal sealed record GrafanaAlert(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("labels")] Dictionary<string, string> Labels,
    [property: JsonPropertyName("annotations")] Dictionary<string, string> Annotations,
    [property: JsonPropertyName("startsAt")] DateTimeOffset? StartsAt,
    [property: JsonPropertyName("fingerprint")] string? Fingerprint,
    [property: JsonPropertyName("silenceURL")] string? SilenceUrl);
