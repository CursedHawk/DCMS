using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

namespace Dcms.PluginSdk.Runtime.Hosting;

/// <summary>
/// What the admin console needs to show plugins' own screens (ADR 0019).
///
/// <para><c>GET /api/admin/plugin-ui</c>: every plugin with where its admin UI module comes from,
/// its declared screens (and whether the caller may open each) and the tenant's instances of it,
/// enabled or not. Built-in plugins' modules are compiled into the console and named by
/// assembly; an installed plugin's is <c>admin/index.js</c> in its folder, served by
/// <c>GET /api/admin/plugin-ui/assets/{pluginId}/{path}</c>.</para>
///
/// <para>The asset route is anonymous: it serves code the operator installed, the same for
/// every tenant, and no tenant data. Only files under the plugin's own <c>admin/</c> folder.</para>
/// </summary>
public static class PluginUiEndpoints
{
    public const string AssetsPrefix = "/api/admin/plugin-ui/assets";
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static IEndpointRouteBuilder MapDcmsPluginUiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/plugin-ui", async (
            HttpContext http, PluginRegistry registry, CmsDbContext db, CancellationToken ct) =>
        {
            var instances = await db.PluginInstances.AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => new { p.Id, p.PluginId, p.Slug, p.Name, p.Enabled, p.AiToolsEnabled })
                .ToListAsync(ct);
            var authz = http.RequestServices.GetService(typeof(IAuthorizationService)) as IAuthorizationService;

            var result = new List<object>();
            foreach (var plugin in registry.Plugins)
            {
                var manifest = plugin.Manifest;
                var screens = new List<object>();
                foreach (var screen in manifest.AdminScreens ?? [])
                {
                    var permission = PluginPermissions.Resolve(manifest.Id, screen.Permission ?? PlatformPermissions.PluginsManage);
                    screens.Add(new
                    {
                        id = screen.Id,
                        title = screen.Title,
                        titles = screen.Titles,
                        description = screen.Description,
                        scope = screen.Scope == AdminScreenScope.Instance ? "instance" : "plugin",
                        icon = screen.IconName ?? manifest.IconName,
                        permission,
                        nav = screen.Nav is { } nav ? new { group = nav.Group, order = nav.Order } : null,
                        allowed = authz is not null
                                  && (await authz.AuthorizeAsync(http.User, null, PermissionPolicyProvider.PolicyName(permission))).Succeeded,
                    });
                }
                result.Add(new
                {
                    pluginId = manifest.Id,
                    name = manifest.Name,
                    icon = manifest.IconName,
                    source = registry.SourceOf(manifest.Id),
                    module = Module(registry, plugin),
                    screens,
                    instances = instances.Where(i => i.PluginId == manifest.Id)
                        .Select(i => new { id = i.Id, slug = i.Slug, name = i.Name, enabled = i.Enabled, aiToolsEnabled = i.AiToolsEnabled }),
                });
            }
            return Results.Ok(result);
        }).RequireAuthorization()
          .PermissionExempt("Describes plugins' screens to any member; each screen states whether this caller may open it.");

        app.MapGet(AssetsPrefix + "/{pluginId}/{**path}", (string pluginId, string path, PluginRegistry registry) =>
        {
            if (registry.FindPlugin(pluginId) is not { } plugin || registry.SourceOf(pluginId) != "installed"
                || AdminFolder(plugin) is not { } folder)
            {
                return Results.NotFound();
            }
            var full = Path.GetFullPath(Path.Combine(folder, path));
            if (!full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
            {
                return Results.NotFound();
            }
            var type = ContentTypes.TryGetContentType(full, out var t) ? t : "application/octet-stream";
            // The URL carries the file's version (?v=), so a changed install is fetched anew.
            return Results.File(full, type, enableRangeProcessing: false);
        }).AllowAnonymous()
          .PermissionExempt("Static UI code of operator-installed plugins: the same for every tenant, no tenant data.");

        return app;
    }

    /// <summary>Where the console loads the plugin's admin UI module from, or null when it has none.</summary>
    internal static object? Module(PluginRegistry registry, IPlugin plugin)
    {
        var assembly = plugin.GetType().Assembly;
        if (registry.SourceOf(plugin.Manifest.Id) != "installed")
        {
            // Compiled into the console from src/Plugins/{assembly}/admin; the console knows
            // whether it has one under this name.
            return new { kind = "builtin", key = assembly.GetName().Name };
        }
        if (AdminFolder(plugin) is not { } folder || !File.Exists(Path.Combine(folder, "index.js")))
        {
            return null;
        }
        var version = File.GetLastWriteTimeUtc(Path.Combine(folder, "index.js")).Ticks.ToString("x");
        var css = File.Exists(Path.Combine(folder, "index.css"));
        var id = Uri.EscapeDataString(plugin.Manifest.Id);
        return new
        {
            kind = "url",
            url = $"{AssetsPrefix}/{id}/index.js?v={version}",
            css = css ? $"{AssetsPrefix}/{id}/index.css?v={version}" : null,
        };
    }

    private static string? AdminFolder(IPlugin plugin) =>
        Path.GetDirectoryName(plugin.GetType().Assembly.Location) is { Length: > 0 } dir
            ? Path.GetFullPath(Path.Combine(dir, "admin"))
            : null;
}
