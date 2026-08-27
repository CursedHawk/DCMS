using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Dcms.Shared.Hosting;

/// <summary>
/// The browser-facing half of the trace-id story.
///
/// <para>An RFC 9457 body is the right answer for an API client and the wrong one for a person:
/// a visitor to a tenant site who is shown <c>{"type":"about:blank","status":500,…}</c> has
/// been handed the trace id and no reason to think it means anything. This renders the same
/// facts as a page, with the id presented as the thing to quote.</para>
///
/// <para>Self-contained on purpose — no stylesheet, no script, no font. It has to render when
/// the thing that is broken might be the static asset pipeline, the object store, or the
/// tenant's own site build.</para>
///
/// <para>Everything interpolated is HTML-encoded. The trace id and request id are hex and a
/// Guid today, but they arrive here through a response header, and a rule that depends on a
/// value's current shape is a rule that breaks the first time the shape changes.</para>
/// </summary>
public static class DcmsErrorPage
{
    /// <summary>
    /// True when this request should be answered with a page rather than a problem document:
    /// a browser navigation, not an API call. Both halves matter — a fetch() from a tenant
    /// site's JavaScript sends <c>Accept: */*</c> and wants the JSON.
    /// </summary>
    public static bool PrefersHtml(HttpRequest request)
    {
        if (request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments("/hub", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        foreach (var accept in request.Headers.Accept)
        {
            if (accept?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }
        return false;
    }

    public static async Task WriteAsync(HttpContext context, int statusCode, string? traceId, string? requestId)
    {
        // Set before the first write; after that the headers are gone.
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";

        // Never cached. A 500 cached at a CDN or in the visitor's browser outlives the incident
        // it describes, and would then be served to someone the platform is working fine for.
        context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";

        await context.Response.WriteAsync(Render(statusCode, traceId, requestId), context.RequestAborted);
    }

    private static string Render(int statusCode, string? traceId, string? requestId)
    {
        var (title, message) = statusCode switch
        {
            404 => ("Not found", "This page does not exist, or is no longer published."),
            403 => ("Not available", "You do not have access to this page."),
            429 => ("Too many requests", "Please wait a moment and try again."),
            502 or 503 or 504 => ("Temporarily unavailable", "This site is being served again shortly. Please try in a minute."),
            _ => ("Something went wrong", "The page could not be shown. Nothing you did caused this."),
        };

        var sb = new StringBuilder(2048);
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<meta name=\"robots\" content=\"noindex\">");
        sb.Append("<title>").Append(Enc(title)).Append("</title><style>");
        sb.Append(":root{color-scheme:light dark}");
        sb.Append("body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;");
        sb.Append("font:16px/1.6 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;background:#fafafa;color:#18181b}");
        sb.Append("main{max-width:34rem;padding:2.5rem 1.5rem;text-align:center}");
        sb.Append("h1{font-size:1.5rem;font-weight:600;margin:0 0 .5rem}");
        sb.Append("p{margin:0 0 1.5rem;color:#52525b}");
        sb.Append("dl{margin:0;padding:1rem;border:1px solid #e4e4e7;border-radius:.5rem;text-align:left;background:#fff}");
        sb.Append("dt{font-size:.75rem;text-transform:uppercase;letter-spacing:.05em;color:#71717a}");
        sb.Append("dd{margin:.15rem 0 .75rem;font-family:ui-monospace,SFMono-Regular,Menlo,monospace;");
        sb.Append("font-size:.8125rem;word-break:break-all;user-select:all}dd:last-child{margin-bottom:0}");
        sb.Append("@media(prefers-color-scheme:dark){body{background:#09090b;color:#fafafa}p{color:#a1a1aa}");
        sb.Append("dl{background:#18181b;border-color:#27272a}dt{color:#a1a1aa}}");
        sb.Append("</style></head><body><main>");
        sb.Append("<h1>").Append(Enc(title)).Append("</h1>");
        sb.Append("<p>").Append(Enc(message)).Append("</p>");

        if (!string.IsNullOrEmpty(traceId) || !string.IsNullOrEmpty(requestId))
        {
            // Selectable rather than copy-buttoned: a copy button needs script, and this page
            // must render when nothing else on the platform does. `user-select:all` above makes
            // one click select the whole id.
            sb.Append("<dl>");
            if (!string.IsNullOrEmpty(traceId))
            {
                sb.Append("<dt>Trace ID — quote this if you report the problem</dt><dd>")
                  .Append(Enc(traceId)).Append("</dd>");
            }
            if (!string.IsNullOrEmpty(requestId))
            {
                sb.Append("<dt>Request ID</dt><dd>").Append(Enc(requestId)).Append("</dd>");
            }
            sb.Append("</dl>");
        }

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static string Enc(string value) => WebUtility.HtmlEncode(value);
}
