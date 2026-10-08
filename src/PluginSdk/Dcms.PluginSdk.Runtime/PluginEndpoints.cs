using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Audit;
using Microsoft.AspNetCore.Authorization;
using Dcms.Shared.Security.Authorization;
using Dcms.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Runtime;

/// <summary>
/// Mounts plugins' bespoke routes. Each plugin gets its own route group under
/// <c>{prefix}/{slug}</c> guarded by <see cref="PluginInstanceFilter"/>, so a handler only runs
/// for an enabled instance of its own plugin and always finds an <see cref="IPluginContext"/>.
///
/// <para>Two plugins mapping the same method and pattern would make routing ambiguous at
/// request time; that is refused at startup instead.</para>
/// </summary>
public static class PluginEndpoints
{
    /// <summary>Site plane (content-api): <c>/api/{slug}/...</c>.</summary>
    public static IEndpointRouteBuilder MapDcmsPluginSiteEndpoints(this IEndpointRouteBuilder app) =>
        Map(app, "/api/{slug}", admin: false);

    /// <summary>Admin plane (admin-api): <c>/api/admin/plugins/{slug}/...</c>.</summary>
    /// <remarks>Returns the enclosing group so the host can add its member authentication.</remarks>
    public static RouteGroupBuilder MapDcmsPluginAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var root = app.MapGroup("/api/admin/plugins/{slug}");
        Map(root, string.Empty, admin: true);
        return root;
    }

    /// <summary>
    /// Every plugin's <see cref="IPluginRequestGate"/>, in registration order. Place after
    /// authentication: a gate sees the request's tenant, user and routed endpoint.
    /// </summary>
    public static IApplicationBuilder UseDcmsPluginGates(this IApplicationBuilder app)
    {
        foreach (var gate in app.ApplicationServices.GetServices<IPluginRequestGate>())
        {
            app.Use(next => http => gate.InvokeAsync(http, next));
        }
        return app;
    }

    /// <summary>Each plugin's <see cref="IPlugin.MapHostEndpoints"/>: routes outside any instance prefix.</summary>
    public static IEndpointRouteBuilder MapDcmsPluginHostEndpoints(this IEndpointRouteBuilder app)
    {
        var registry = app.ServiceProvider.GetRequiredService<PluginRegistry>();
        var host = app.ServiceProvider.GetRequiredService<PluginHost>();
        foreach (var plugin in registry.Plugins)
        {
            // An empty-prefix group, so the plugin's route conventions can be applied and its
            // handlers get a tenant-wide IPluginContext (no instance) like every other plugin code.
            var group = app.MapGroup(string.Empty)
                .AddEndpointFilter(new PluginHostContextFilter(plugin.Manifest.Id))
                .AddEndpointFilter(PluginPermissionConvention.DeclareAudit);
            plugin.MapHostEndpoints(group, host);
            PluginPermissionConvention.Apply(group, plugin.Manifest);
        }
        return app;
    }

    private static IEndpointRouteBuilder Map(IEndpointRouteBuilder app, string prefix, bool admin)
    {
        var registry = app.ServiceProvider.GetRequiredService<PluginRegistry>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in registry.Plugins)
        {
            var pluginId = plugin.Manifest.Id;
            var group = app.MapGroup(prefix)
                .AddEndpointFilter(new PluginInstanceFilter(pluginId))
                .AddEndpointFilter(PluginPermissionConvention.DeclareAudit);
            var builder = new LiveEndpointBuilder(group, pluginId, seen, allowContentRoutes: !admin);
            if (admin)
            {
                plugin.MapAdminEndpoints(builder);
            }
            else
            {
                plugin.MapEndpoints(builder);
            }
            PluginPermissionConvention.Apply(group, plugin.Manifest);
        }
        return app;
    }

    private sealed class LiveEndpointBuilder(
        RouteGroupBuilder group, string pluginId, Dictionary<string, string> seen, bool allowContentRoutes)
        : IPluginEndpointBuilder
    {
        // Content routes are recorded by PluginRouteTable and served generically by delivery.
        public void MapContentList(string contentType, Action<ContentQueryOptions>? configure = null) => ContentRoute();

        public void MapContentGetBySlug(string contentType) => ContentRoute();

        public RouteHandlerBuilder MapGet(string pattern, Delegate handler) => Add("GET", pattern, () => group.MapGet(pattern, handler));
        public RouteHandlerBuilder MapPost(string pattern, Delegate handler) => Add("POST", pattern, () => group.MapPost(pattern, handler));
        public RouteHandlerBuilder MapPut(string pattern, Delegate handler) => Add("PUT", pattern, () => group.MapPut(pattern, handler));
        public RouteHandlerBuilder MapDelete(string pattern, Delegate handler) => Add("DELETE", pattern, () => group.MapDelete(pattern, handler));
        public RouteHandlerBuilder MapPatch(string pattern, Delegate handler) => Add("PATCH", pattern, () => group.MapPatch(pattern, handler));

        private RouteHandlerBuilder Add(string method, string pattern, Func<RouteHandlerBuilder> map)
        {
            if (!pattern.StartsWith('/'))
            {
                throw new InvalidOperationException($"Plugin '{pluginId}' route '{pattern}' must start with '/'.");
            }
            var key = $"{method} {pattern.TrimEnd('/')}";
            if (seen.TryGetValue(key, out var owner))
            {
                throw new InvalidOperationException(
                    $"Plugins '{owner}' and '{pluginId}' both map {key}; plugin routes must be unique.");
            }
            seen[key] = pluginId;
            return map().WithTags($"plugin:{pluginId}");
        }

        private void ContentRoute()
        {
            if (!allowContentRoutes)
            {
                throw new InvalidOperationException(
                    $"Plugin '{pluginId}' declares content routes in MapAdminEndpoints; they belong in MapEndpoints.");
            }
        }
    }
}

/// <summary>
/// For a plugin's host routes: when the request carries a workspace, establishes the plugin's
/// tenant-wide <see cref="IPluginContext"/> (no instance) so the handler can reach its contracts
/// — its storage, its events — as anywhere else. A route without a workspace (an OAuth callback)
/// gets none and resolves what it needs itself.
/// </summary>
public sealed class PluginHostContextFilter(string pluginId) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var accessor = http.RequestServices.GetRequiredService<PluginContextAccessor>();
        if (accessor.Current is null && http.RequestServices.GetRequiredService<ITenantContext>().TenantId is { } tenantId)
        {
            var factory = http.RequestServices.GetRequiredService<PluginContextFactory>();
            accessor.Current = await factory.CreateAsync(tenantId, pluginId, null, factory.CurrentActor(), http.RequestAborted);
        }
        return await next(context);
    }
}

/// <summary>
/// Resolves <c>{slug}</c> to an enabled instance of one plugin for the request's tenant and
/// establishes the <see cref="IPluginContext"/> for the handler. Anything else is a 404: a
/// disabled instance, another plugin's slug, and no tenant all look the same from outside.
/// </summary>
public sealed class PluginInstanceFilter(string pluginId) : IEndpointFilter
{
    public string PluginId => pluginId;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var tenant = http.RequestServices.GetRequiredService<ITenantContext>();
        if (tenant.TenantId is not { } tenantId || http.GetRouteValue("slug") is not string slug)
        {
            return Results.NotFound();
        }

        var factory = http.RequestServices.GetRequiredService<PluginContextFactory>();
        var enabled = await factory.EnabledInstancesAsync(tenantId, http.RequestAborted);
        var instance = enabled.FirstOrDefault(i =>
            i.PluginId == pluginId && string.Equals(i.Slug, slug, StringComparison.Ordinal));
        if (instance is null)
        {
            return Results.NotFound();
        }

        var pluginContext = await factory.CreateAsync(tenantId, pluginId, instance, factory.CurrentActor(), http.RequestAborted);
        http.RequestServices.GetRequiredService<PluginContextAccessor>().Current = pluginContext;
        return await next(context);
    }
}

/// <summary>
/// Turns the SDK's route conventions (<see cref="PluginEndpointConventions"/>) into the platform's
/// own: a required permission becomes the permission gate, a public route a permission
/// exemption, an audit name the audit action <c>plugin.{id}.{action}</c>. Runs as a
/// <c>Finally</c> convention so it sees metadata the plugin added after mapping the route; a
/// permission must be one the plugin or the platform declares.
/// </summary>
internal static class PluginPermissionConvention
{
    /// <summary>
    /// For a route named with <c>AuditAs</c>: opens its audit entry before the handler runs, as
    /// <c>WithAudit</c> does, so the request is recorded under the plugin's name even when the
    /// handler's contract calls record entries of their own.
    /// </summary>
    public static ValueTask<object?> DeclareAudit(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<PluginAuditMetadata>() is not null)
        {
            AuditEndpointExtensions.Declare(context.HttpContext);
        }
        return next(context);
    }

    public static void Apply(IEndpointConventionBuilder group, PluginManifest manifest) =>
        group.Finally(endpoint =>
        {
            if (endpoint.Metadata.OfType<PluginPermissionMetadata>().LastOrDefault() is { } required)
            {
                var key = PluginPermissions.Resolve(manifest.Id, required.Permission);
                if (!PluginRegistry.DeclaresPermission(manifest, key))
                {
                    throw new InvalidOperationException(
                        $"Plugin '{manifest.Id}' route {endpoint.DisplayName} requires '{key}', which neither the plugin nor the platform declares.");
                }
                endpoint.Metadata.Add(new PermissionMetadata(key));
                endpoint.Metadata.Add(new AuthorizeAttribute(PermissionPolicyProvider.PolicyName(key)));
            }
            if (endpoint.Metadata.OfType<PluginPublicRouteMetadata>().LastOrDefault() is { } open)
            {
                endpoint.Metadata.Add(new PermissionExemptMetadata($"Plugin '{manifest.Id}': {open.Reason}"));
            }
            if (endpoint.Metadata.OfType<PluginAuditMetadata>().LastOrDefault() is { } audit)
            {
                endpoint.Metadata.Add(new AuditMetadata(AuditActions.ForPlugin(manifest.Id, audit.Action), null, AuditCategory.TenantState));
            }
            if (endpoint.Metadata.OfType<PluginAuditExemptMetadata>().LastOrDefault() is { } skip)
            {
                endpoint.Metadata.Add(new AuditExemptMetadata($"Plugin '{manifest.Id}': {skip.Reason}"));
            }
        });
}
