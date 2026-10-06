using Microsoft.AspNetCore.Builder;

namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// Endpoint surface handed to a plugin. Every route is implicitly tenant- and instance-scoped:
/// patterns are relative to <c>/api/{slug}</c> (site plane) or <c>/api/admin/plugins/{slug}</c>
/// (admin plane), and a request only reaches the handler when <c>{slug}</c> names an enabled
/// instance of <i>this</i> plugin for the tenant. Handlers take <c>IPluginContext</c> from DI.
/// </summary>
public interface IPluginEndpointBuilder
{
    /// <summary>Standard paged list endpoint for a content type (auto-documented).</summary>
    void MapContentList(string contentType, Action<ContentQueryOptions>? configure = null);

    /// <summary>Standard get-by-slug endpoint for a content type (auto-documented).</summary>
    void MapContentGetBySlug(string contentType);

    /// <summary>A bespoke route, e.g. <c>MapPost("/register", handler)</c>.</summary>
    RouteHandlerBuilder MapGet(string pattern, Delegate handler);

    RouteHandlerBuilder MapPost(string pattern, Delegate handler);

    RouteHandlerBuilder MapPut(string pattern, Delegate handler);

    RouteHandlerBuilder MapDelete(string pattern, Delegate handler);

    /// <summary>A partial update: the body carries only what changes.</summary>
    RouteHandlerBuilder MapPatch(string pattern, Delegate handler);
}

public sealed class ContentQueryOptions
{
    public int DefaultPageSize { get; set; } = 20;
    public int MaxPageSize { get; set; } = 100;
}

/// <summary>
/// Says which permission a plugin route needs. The platform enforces it when it mounts the
/// plugin's routes: a caller without it gets 403 (401 when not signed in), and the requirement
/// shows up wherever the platform describes the route.
/// </summary>
/// <param name="Permission">One of the plugin's own actions (<c>"moderate"</c>) or a full key (<c>"content:read"</c>).</param>
public sealed record PluginPermissionMetadata(string Permission);

/// <summary>The route is deliberately not gated by a permission (a public site route), and why.</summary>
public sealed record PluginPublicRouteMetadata(string Reason);

/// <summary>What a state-changing route does, for the audit log: recorded as <c>plugin.{pluginId}.{Action}</c>.</summary>
public sealed record PluginAuditMetadata(string Action);

/// <summary>A state-changing route that is deliberately not audited per request, and why.</summary>
public sealed record PluginAuditExemptMetadata(string Reason);

/// <summary>
/// How a plugin states what gates and records its routes — with the SDK alone, so a plugin built
/// outside this repository can. The platform's coverage tests hold every route to having said
/// both: who may call it, and what the audit log calls it when it changes something.
/// </summary>
public static class PluginEndpointConventions
{
    /// <summary>A route anyone may call (a site visitor signing a guestbook), with the reason.</summary>
    public static TBuilder WithoutPermission<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new PluginPublicRouteMetadata(reason));
        return builder;
    }

    /// <summary>Names the change a route makes in the audit log: <c>.AuditAs("entry.approved")</c> records <c>plugin.{id}.entry.approved</c>.</summary>
    public static TBuilder AuditAs<TBuilder>(this TBuilder builder, string action)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new PluginAuditMetadata(action));
        return builder;
    }

    /// <summary>A state-changing route whose requests are not worth a record each (it records something better itself, or changes nothing a tenant owns).</summary>
    public static TBuilder SkipAudit<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new PluginAuditExemptMetadata(reason));
        return builder;
    }

    /// <summary>
    /// Requires a permission for this route: <c>endpoints.MapPost("/approve", …).RequirePluginPermission("moderate")</c>.
    /// Works on site-plane, admin-plane and host routes alike; members only ever hold permissions in the admin.
    /// </summary>
    public static TBuilder RequirePluginPermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new PluginPermissionMetadata(permission));
        return builder;
    }
}
