using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Kernel.Abstractions;
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

    private static IEndpointRouteBuilder Map(IEndpointRouteBuilder app, string prefix, bool admin)
    {
        var registry = app.ServiceProvider.GetRequiredService<PluginRegistry>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in registry.Plugins)
        {
            var pluginId = plugin.Manifest.Id;
            var group = app.MapGroup(prefix).AddEndpointFilter(new PluginInstanceFilter(pluginId));
            var builder = new LiveEndpointBuilder(group, pluginId, seen, allowContentRoutes: !admin);
            if (admin)
            {
                plugin.MapAdminEndpoints(builder);
            }
            else
            {
                plugin.MapEndpoints(builder);
            }
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
