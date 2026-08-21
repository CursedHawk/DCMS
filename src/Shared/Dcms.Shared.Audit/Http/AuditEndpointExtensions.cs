using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Audit.Http;

/// <summary>
/// Endpoint metadata naming what an endpoint does, in audit terms.
///
/// <para>Attaching this is enough on its own: the middleware turns it into a record even when
/// the handler body says nothing about auditing. A handler that wants to add a resource label,
/// a count, or a field diff records explicitly instead, and its entry supersedes the generic
/// one.</para>
/// </summary>
/// <param name="ResourceType">
/// The kind of thing the endpoint acts on ("site", "role"). Also lets the middleware pick the
/// resource id out of the route without the handler restating it.
/// </param>
public sealed record AuditMetadata(
    string Action,
    string? ResourceType = null,
    AuditCategory Category = AuditCategory.TenantState);

/// <summary>
/// Marks an endpoint as deliberately unaudited. Used for the handful of routes where a record
/// would be pure noise — and it is a *declaration*, not silence: the endpoint coverage test
/// accepts this and rejects an endpoint that simply forgot.
/// </summary>
/// <param name="Reason">Why. Read by whoever revisits the exemption later.</param>
public sealed record AuditExemptMetadata(string Reason);

public static class AuditEndpointExtensions
{
    /// <summary>
    /// Names the action an endpoint performs, so its records read as
    /// <c>site.deleted</c> rather than <c>http.delete./api/admin/sites/{id}</c>.
    /// Sits next to <c>RequirePermission</c> in the same fluent chain.
    ///
    /// <para>The entry is opened <b>before the handler runs</b>, not after the response. That
    /// is what lets the record commit inside the handler's own transaction: whatever
    /// <c>SaveChangesAsync</c> the handler calls sweeps the buffered entry into the same
    /// commit as the change it describes, and the EF layer hangs the field diff on it along
    /// the way. Recorded after the fact, it would be a separate write that a crash in between
    /// could lose — which is the failure an audit log exists to rule out.</para>
    ///
    /// <para>An entry that never reaches a save is withdrawn by the middleware if the response
    /// was not a success, so declaring an action does not mean claiming it happened.</para>
    /// </summary>
    public static TBuilder WithAudit<TBuilder>(
        this TBuilder builder,
        string action,
        string? resourceType = null,
        AuditCategory category = AuditCategory.TenantState)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new AuditMetadata(action, resourceType, category));

        builder.AddEndpointFilter(static (context, next) =>
        {
            var http = context.HttpContext;
            var declared = http.GetEndpoint()?.Metadata.GetMetadata<AuditMetadata>();
            if (declared is not null)
            {
                var recorder = http.RequestServices.GetRequiredService<IAuditRecorder>();
                var scope = http.RequestServices.GetRequiredService<AuditScope>();

                // The route is only resolvable once an endpoint has been selected, which is
                // exactly now. Entries that commit inside the handler's transaction are
                // completed before the middleware ever sees the response, so this is their
                // only chance to learn where the request came from.
                scope.Http = AuditHttpInfoFactory.Build(http, includeStatus: false);
                scope.Permission = http.GetEndpoint()?.Metadata.GetMetadata<IAuditPermission>()?.Permission;

                var entry = recorder.Record(declared.Action);
                entry.Category = declared.Category;
                if (declared.ResourceType is { } type)
                {
                    entry.ResourceType = type;
                    entry.ResourceId = RouteId(http, type);
                }
                scope.Declared = entry;
            }

            return next(context);
        });

        return builder;
    }

    /// <summary>
    /// The resource id as the route already states it: a value named "id", else one named
    /// after the resource ("siteId"). Null for a create endpoint, whose route has no id yet —
    /// there the EF layer fills it in from the row that was inserted.
    /// </summary>
    private static string? RouteId(HttpContext context, string resourceType)
    {
        var values = context.Request.RouteValues;
        if (values.TryGetValue("id", out var id) && id is not null)
        {
            return id.ToString();
        }
        return values.TryGetValue($"{resourceType}Id", out var typed) ? typed?.ToString() : null;
    }

    /// <summary>
    /// Declares that an endpoint intentionally produces no audit record, with a reason.
    /// Prefer <see cref="WithAudit{TBuilder}"/>: a coarse record is nearly always better than
    /// none, and this exists for routes that are genuinely not actions on tenant state.
    /// </summary>
    public static TBuilder AuditExempt<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new AuditExemptMetadata(reason));
        return builder;
    }
}
