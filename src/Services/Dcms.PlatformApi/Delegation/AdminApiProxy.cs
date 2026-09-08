using Dcms.Shared.Security;

namespace Dcms.PlatformApi.Delegation;

/// <summary>
/// The console's hop to admin-api, for the four areas platform-api must not reach into the
/// database for.
///
/// <para><b>Why a hop at all.</b> platform-api connects as <c>dcms_platform</c>, which holds
/// USAGE on <c>obs</c> and CRUD on <c>platform</c> and nothing else
/// (<c>infra/postgres/init/04-platform-role.sh</c>). Every route that goes through here exists
/// so that stays true:</para>
/// <list type="bullet">
///   <item>certificates live in the <c>edge</c> schema and reissue publishes a NATS event —
///   granting write there would put TLS material behind the service holding the delete
///   buttons;</item>
///   <item>platform notifications live in <c>notifications</c>, and their read state is keyed
///   on the operator's user id;</item>
///   <item>tenant suspend and resume write <c>tenancy</c> and publish an event site-host
///   depends on — ADR 0003 is unambiguous about who owns that;</item>
///   <item>the analytics prune is a batched delete on <c>analytics.events</c>, and a DELETE
///   grant there is exactly what the platform role script refuses.</item>
/// </list>
///
/// <para>The audit log is the one the console reads <b>directly</b>: <c>obs.v_audit_recent</c>
/// already exposes a superset of what the page renders and <c>dcms_platform</c> already has
/// SELECT on it, so a hop there would buy nothing.</para>
///
/// <para><b>The response is relayed verbatim</b> — status, content type and body. Re-modelling
/// each payload here would be a second copy of admin-api's contract that drifts silently; the
/// console's job is to authorize the operator and get out of the way. A ProblemDetails from
/// admin-api reaches the browser as itself, with its own trace id intact.</para>
/// </summary>
public sealed class AdminApiProxy(HttpClient http, IServiceTokenProvider tokens, ILogger<AdminApiProxy> logger)
{
    /// <summary>Named so the typed client and its handlers can be registered by name.</summary>
    public const string HttpClientName = "admin-api";

    /// <summary>
    /// The scope this hop is made with. Not <c>dcms.admin</c> and not the client content-api
    /// holds: a token that reached those would carry <c>dcms.ai</c> and <c>dcms.social</c> too.
    /// </summary>
    public const string Scope = "dcms.console";

    /// <summary>
    /// Forwards one request and writes admin-api's answer straight back to the caller.
    ///
    /// <para>Who the operator is travels in the propagation headers the client's
    /// <c>AuditPropagationHandler</c> stamps, not in the token — see
    /// <c>PropagatedActorMiddleware</c> on the far side for why a service token alone would
    /// make every record name this service.</para>
    /// </summary>
    public async Task ForwardAsync(
        HttpMethod method, string path, HttpContext context, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);

        if (method != HttpMethod.Get && method != HttpMethod.Delete)
        {
            // Buffered rather than streamed: these bodies are a handful of fields, and a
            // streamed content on a request that gets retried by a handler is not re-readable.
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            request.Content = new ByteArrayContent(buffer.ToArray());
            request.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await tokens.GetTokenAsync(Scope, ct));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 502, not 500: this service is fine and the console should say which side is not.
            logger.LogWarning(ex, "admin-api did not answer {Method} {Path}.", method, path);
            await Results.Problem(
                title: "The admin service could not be reached.",
                detail: "This page is served by admin-api through the console. Try again shortly.",
                statusCode: StatusCodes.Status502BadGateway)
                .ExecuteAsync(context);
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } contentType)
            {
                context.Response.ContentType = contentType.ToString();
            }

            await response.Content.CopyToAsync(context.Response.Body, ct);
        }
    }
}
