using System.Diagnostics;
using Dcms.Shared.Audit.Propagation;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Audit.Http;

/// <summary>
/// Endpoint metadata that names the permission gating an endpoint. Implemented by
/// <c>PermissionMetadata</c> in Dcms.Shared.Security, so the audit middleware can record which
/// key allowed (or refused) an action without the audit library depending on the authorization
/// stack.
/// </summary>
public interface IAuditPermission
{
    string Permission { get; }
}

/// <summary>
/// Establishes the ambient audit envelope for a request, and flushes what was recorded.
///
/// <para>Also the only place that knows a request was <b>refused</b>. Roughly ninety endpoints
/// go through the permission policy, but the SuperAdmin-only ones check
/// <c>if (!me.IsSuperAdmin) return Results.Forbid();</c> imperatively and never reach an
/// authorization handler. Keying off the response status instead of the authorization pipeline
/// catches both shapes, and any future third shape as well.</para>
///
/// <para>Must be registered <b>after</b> UseForwardedHeaders — otherwise the client address it
/// records is the proxy's. That is also why this is wired explicitly in each Program.cs rather
/// than through an IStartupFilter, which would wrap the pipeline from the outside.</para>
/// </summary>
public sealed class AuditMiddleware(RequestDelegate next)
{
    public const string CorrelationHeader = "X-Dcms-Request-Id";
    private const string SandboxHeader = "X-Dcms-Sandbox";

    public async Task InvokeAsync(
        HttpContext context,
        IAuditRecorder recorder,
        AuditScope scope,
        ICurrentActor actor,
        AuditAmbient ambient)
    {
        // Resolved lazily, not now: this middleware sits before authentication so that a
        // refusal is still inside the scope, which means the principal does not exist yet.
        scope.ActorResolver = () => new AuditActor(
            actor.Kind, actor.Id, actor.Key, actor.Display, AuditAttribution.Direct);

        // Makes the scope reachable from the singleton event publisher, so anything this
        // request queues carries the person who asked for it across the bus.
        using var ambientScope = ambient.Enter(scope);

        var correlationId = ResolveCorrelationId(context);
        scope.CorrelationId = correlationId;
        scope.TraceId = Activity.Current?.TraceId.ToString();
        scope.SpanId = Activity.Current?.SpanId.ToString();
        scope.IsSandbox = context.Request.Headers.ContainsKey(SandboxHeader);

        // Echoed before the response starts, so a caller can quote it in a support ticket even
        // when the request goes on to fail.
        context.Response.Headers[CorrelationHeader] = correlationId;

        if (IsUninteresting(context))
        {
            scope.SuppressFallback = true;
            await next(context);
            return;
        }

        try
        {
            await next(context);
        }
        finally
        {
            scope.Permission = FindPermission(context);
            scope.Http = AuditHttpInfoFactory.Build(context, includeStatus: true);

            WithdrawUnfulfilledDeclaration(context, recorder, scope);
            RecordDenial(context, recorder, scope);
            RecordFallback(context, recorder, scope);

            await recorder.FlushAsync(context.RequestAborted);
        }
    }

    /// <summary>
    /// Takes back the entry <c>.WithAudit(...)</c> opened before the handler ran, when the
    /// handler then did not do the thing.
    ///
    /// <para>The test is not "did the handler fail" but "did anything commit". An entry that
    /// reached a <c>SaveChanges</c> is already enlisted in a transaction — if that transaction
    /// committed, the change is real and the record belongs with it; if it rolled back, the
    /// record rolled back too. Only an entry still sitting in the buffer is a claim about
    /// something that demonstrably did not happen, and only then does the response status
    /// decide.</para>
    ///
    /// <para>Only 4xx and 5xx withdraw. A 2xx with no database write is still an action — an
    /// object-store put, a git push, a DNS record — and so is a 3xx, which on this platform is
    /// how a successful form post answers: the sign-in that redirects to the dashboard is the
    /// most consequential redirect there is.</para>
    /// </summary>
    private static void WithdrawUnfulfilledDeclaration(HttpContext context, IAuditRecorder recorder, AuditScope scope)
    {
        if (scope.Declared is not { } declared || !recorder.Pending.Contains(declared))
        {
            return;
        }

        if (context.Response.StatusCode < StatusCodes.Status400BadRequest)
        {
            return;
        }

        recorder.Discard(declared);
    }

    /// <summary>
    /// A refusal is worth a record whatever produced it — a policy failure, an imperative
    /// SuperAdmin check, or an expired token. The distinction between "not signed in" and
    /// "signed in and not allowed" is the first thing anyone investigating wants.
    /// </summary>
    private static void RecordDenial(HttpContext context, IAuditRecorder recorder, AuditScope scope)
    {
        var status = context.Response.StatusCode;
        if (status is not (StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden))
        {
            return;
        }

        var action = status == StatusCodes.Status401Unauthorized
            ? AuditActions.Unauthenticated
            : AuditActions.PermissionDenied;

        var entry = recorder.Record(action)
            .As(AuditCategory.Security, AuditSeverity.Notice);
        entry.Outcome = AuditOutcome.Denied;

        if (scope.Permission is not null)
        {
            entry.With("permission", scope.Permission);
        }
    }

    /// <summary>
    /// The record for a state-changing request that recorded nothing itself.
    ///
    /// <para>This is what makes coverage structural rather than a matter of everyone
    /// remembering. A new endpoint that nobody thought about still produces a record — coarse,
    /// but naming the actor, the tenant, the route and the outcome, which is most of what an
    /// investigation needs. Missing semantics degrade the log; they never blank it.</para>
    ///
    /// <para>Endpoints improve on this in two steps: <c>.WithAudit(...)</c> supplies a real
    /// action name with no change to the handler, and recording explicitly in the handler adds
    /// the resource label, counts and field diffs.</para>
    /// </summary>
    private static void RecordFallback(HttpContext context, IAuditRecorder recorder, AuditScope scope)
    {
        if (scope.HasExplicitRecord || scope.SuppressFallback)
        {
            return;
        }

        // Reads are covered by the sensitive-read records that endpoints add deliberately;
        // recording every GET would bury the changes under navigation noise.
        if (HttpMethods.IsGet(context.Request.Method)
            || HttpMethods.IsHead(context.Request.Method)
            || HttpMethods.IsOptions(context.Request.Method))
        {
            return;
        }

        // Only requests that got somewhere. A 4xx that is not a refusal changed nothing, and
        // refusals already have their own record from RecordDenial. Redirects count: a form
        // post that answers with one has done its work.
        if (context.Response.StatusCode >= StatusCodes.Status400BadRequest)
        {
            return;
        }

        var endpoint = context.GetEndpoint();
        var declared = endpoint?.Metadata.GetMetadata<AuditMetadata>();
        var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText;

        var entry = recorder.Record(
            declared?.Action ?? AuditActions.ForRequest(context.Request.Method, route));
        entry.Category = declared?.Category ?? AuditCategory.TenantState;

        if (declared?.ResourceType is { } resourceType)
        {
            entry.ResourceType = resourceType;
            entry.ResourceId = FindRouteId(context, resourceType);
        }
    }

    /// <summary>
    /// Pulls the resource id out of the route so a declared endpoint does not have to restate
    /// what its own path already says. Looks for a route value named "id", then one named after
    /// the resource ("siteId"). Returns null when neither is present — a create endpoint has no
    /// id in its route, and the handler is the only thing that can supply one.
    /// </summary>
    private static string? FindRouteId(HttpContext context, string resourceType)
    {
        var values = context.Request.RouteValues;
        if (values.TryGetValue("id", out var id) && id is not null)
        {
            return id.ToString();
        }
        return values.TryGetValue($"{resourceType}Id", out var typed) ? typed?.ToString() : null;
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        // Honour an inbound id so a browser action, the admin API call it triggers, and any
        // work that call queues all carry one identifier.
        if (context.Request.Headers.TryGetValue(CorrelationHeader, out var supplied))
        {
            var value = supplied.ToString();
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 64)
            {
                return value;
            }
        }
        return Guid.CreateVersion7().ToString("N");
    }

    private static string? FindPermission(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<IAuditPermission>()?.Permission;

    private static bool IsUninteresting(HttpContext context)
    {
        var path = context.Request.Path;
        return path.StartsWithSegments("/health") || path.StartsWithSegments("/metrics");
    }
}

public static class AuditApplicationBuilderExtensions
{
    /// <summary>
    /// Adds correlation and audit capture. Place it <b>after</b> <c>UseForwardedHeaders()</c>
    /// and before authentication, so refusals from the auth stack are still inside the scope.
    /// </summary>
    public static IApplicationBuilder UseDcmsAudit(this IApplicationBuilder app) =>
        app.UseMiddleware<AuditMiddleware>();
}
