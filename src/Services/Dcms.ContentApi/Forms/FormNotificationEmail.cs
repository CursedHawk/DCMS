using System.Net;
using Dcms.Shared.Messaging.Email;

namespace Dcms.ContentApi.Forms;

/// <summary>One notification email to send, fully rendered from the submission.</summary>
/// <param name="Recipients">Addresses from the form's notify config.</param>
/// <param name="ReplyTo">Visitor address to reply to, when the form names an email field.</param>
/// <param name="Values">Declared fields in form order, as (label, display value) pairs.</param>
/// <param name="SubmissionId">Doubles as the queue's idempotency key, so a retried enqueue can't double-send.</param>
public sealed record FormNotificationMessage(
    IReadOnlyList<string> Recipients,
    string Subject,
    string FormTitle,
    string InstanceName,
    Guid SubmissionId,
    Guid TenantId,
    DateTimeOffset SubmittedAt,
    IReadOnlyList<(string Label, string Value)> Values,
    string? ReplyTo);

/// <summary>
/// Turns a submission notification into a queued email. Rendering happens here, on
/// the producer side, because only content-api knows what a submission looks like —
/// email-worker just delivers whatever body it is handed.
/// </summary>
public static class FormNotificationEmail
{
    public const string Purpose = "form-notification";

    public static EmailMessage ToEmail(this FormNotificationMessage message) => new(
        Recipients: message.Recipients,
        Subject: message.Subject,
        HtmlBody: RenderBody(message),
        ReplyTo: message.ReplyTo,
        Purpose: Purpose,
        TenantId: message.TenantId,
        DedupeKey: $"form-submission:{message.SubmissionId}");

    private static string RenderBody(FormNotificationMessage message)
    {
        var rows = string.Concat(message.Values.Select(v => $"""
            <tr>
              <th style="text-align:left;padding:.4rem .75rem .4rem 0;vertical-align:top;color:#475569;font-weight:600;white-space:nowrap">{Enc(v.Label)}</th>
              <td style="padding:.4rem 0;vertical-align:top;white-space:pre-wrap">{Enc(v.Value)}</td>
            </tr>
            """));

        return $"""
            <div style="font-family:system-ui,sans-serif;color:#0f172a;line-height:1.5">
              <h2 style="font-size:1.1rem">New {Enc(message.FormTitle)} submission</h2>
              <p style="font-size:.85rem;color:#475569">{Enc(message.InstanceName)} · {Enc(message.SubmittedAt.UtcDateTime.ToString("u"))}</p>
              <table style="border-collapse:collapse;margin:1rem 0">{rows}</table>
              <p style="font-size:.85rem;color:#475569">Review and manage submissions under Forms in the DCMS admin.</p>
            </div>
            """;
    }

    private static string Enc(string value) => WebUtility.HtmlEncode(value);
}
