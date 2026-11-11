using Dcms.Shared.Data.Tenancy;
using Dcms.AdminApi.Tenancy;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Caching;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Dcms.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Plugins;

public static class PluginInstanceEndpoints
{
    public static IEndpointRouteBuilder MapPluginEndpoints(this IEndpointRouteBuilder app)
    {
        // Catalog of installed plugins (manifests) for config forms + permission UI.
        app.MapGet("/api/admin/plugins/catalog", (IPluginCatalog catalog) =>
            Results.Ok(catalog.Manifests.Select(ToManifestDto)))
            .RequireAuthorization();

        app.MapGet("/api/admin/plugins/instances", async (CmsDbContext db, CancellationToken ct) =>
        {
            var instances = await db.PluginInstances
                .OrderBy(p => p.Slug)
                .Select(p => new
                {
                    id = p.Id,
                    pluginId = p.PluginId,
                    slug = p.Slug,
                    name = p.Name,
                    description = p.Description,
                    enabled = p.Enabled,
                    aiToolsEnabled = p.AiToolsEnabled,
                    // The config drives more than its own edit form: a content type
                    // with admin-defined fields keeps their definitions here, and
                    // Events resolves its Roster instance through rosterSlug.
                    config = p.ConfigJson,
                })
                .ToListAsync(ct);
            return Results.Ok(instances);
        }).RequirePermission(PlatformPermissions.PluginsManage);

        // Install, configure, enable: PluginInstanceService, shared with dcms.plugins@1.
        app.MapPost("/api/admin/plugins/instances", (CreateInstanceRequest body, PluginInstanceService plugins, CancellationToken ct) =>
            Run(async () =>
            {
                var instance = await plugins.CreateAsync(body.PluginId, body.Slug, body.Name, body.Description, body.Config, ct);
                return Results.Created($"/api/admin/plugins/instances/{instance.Id}", new { id = instance.Id });
            })).RequirePermission(PlatformPermissions.PluginsManage).WithAudit(AuditActions.PluginInstanceCreated, "plugin_instance")
            .ManagedByDeployments("plugins");

        app.MapPut("/api/admin/plugins/instances/{id:guid}", (Guid id, UpdateInstanceRequest body, PluginInstanceService plugins, CancellationToken ct) =>
            Run(async () => await plugins.UpdateAsync(id, body.Name, body.Description, body.Config, body.AiToolsEnabled, ct) is null
                ? Results.NotFound()
                : Results.NoContent()))
            .RequirePermission(PlatformPermissions.PluginsManage).WithAudit(AuditActions.PluginInstanceUpdated, "plugin_instance")
            .ManagedByDeployments("plugins");

        app.MapPost("/api/admin/plugins/instances/{id:guid}/{action}", (Guid id, string action, PluginInstanceService plugins, CancellationToken ct) =>
            action is not ("enable" or "disable")
                ? Task.FromResult(Results.NotFound())
                : Run(async () => await plugins.SetEnabledAsync(id, action == "enable", ct) is null ? Results.NotFound() : Results.NoContent()))
            .RequirePermission(PlatformPermissions.PluginsManage).WithAudit(AuditActions.PluginInstanceActioned, "plugin_instance")
            .ManagedByDeployments("plugins");

        return app;
    }

    private static async Task<IResult> Run(Func<Task<IResult>> call)
    {
        try
        {
            return await call();
        }
        catch (PluginInstanceRefusal refusal)
        {
            return Results.Json(refusal.Body, statusCode: refusal.Status);
        }
    }

    /// <summary>
    /// The single place a plugin instance change leaves this service, which is why the counter
    /// lives here rather than at the three call sites: a fourth kind of change added later gets
    /// counted by construction instead of by remembering.
    /// </summary>
    internal static async Task PublishChange(IEventPublisher events, ICacheService cache, DcmsMetrics metrics, PluginInstance instance, PluginInstanceChangeKind kind, CancellationToken ct)
    {
        // Plugin id, not instance id: the catalogue is a fixed, small set, whereas instances
        // are created per tenant without bound.
        metrics.PluginInstanceChanged(instance.PluginId, kind.ToString());
        // Every process's cached instance list for the tenant is now stale (PluginInstanceCache).
        await PluginInstanceCache.BumpAsync(cache, instance.TenantId, ct);
        await events.PublishAsync(Subjects.PluginInstanceChanged,
            new PluginInstanceChanged(Guid.NewGuid(), DateTimeOffset.UtcNow, instance.TenantId, instance.Id, instance.PluginId, kind), ct);
    }

    private static object ToManifestDto(PluginManifest m) => new
    {
        id = m.Id,
        name = m.Name,
        version = m.Version,
        description = m.Description,
        allowMultipleInstances = m.AllowMultipleInstances,
        configJsonSchema = m.ConfigJsonSchema,
        permissions = m.Permissions.Select(p => new { p.Action, p.DisplayName }),
        // Contracts (docs/adr/0016): what this plugin offers and what it needs.
        provides = (m.Provides ?? []).Select(p => ContractIds.Of(p.Contract)),
        consumes = (m.Consumes ?? []).Select(c => new { c.ContractId, c.Optional, c.BindingConfigKey }),
        publicConfigKeys = m.PublicConfigKeys,
        contentTypes = m.ContentTypes.Select(t => new
        {
            t.Name,
            t.Searchable,
            t.SlugField,
            // Tells the editor to render the admin-defined fields declared in the
            // instance config alongside the plugin's own.
            customFields = t.CustomFields is null ? null : new
            {
                t.CustomFields.ValuesField,
                t.CustomFields.ConfigKey,
            },
            // Field definitions drive the schema-driven content editor in the SPA.
            fields = t.Fields.Select(f => new
            {
                f.Name,
                type = f.Type.ToString(),
                f.Required,
                f.Description,
                reference = f.Reference is null ? null : new
                {
                    f.Reference.TargetPluginId,
                    f.Reference.ContentType,
                    mediaCategory = f.Reference.MediaCategory?.ToString(),
                },
            }),
        }),
    };

    private sealed record CreateInstanceRequest(string PluginId, string Slug, string? Name, string? Description, string? Config);
    private sealed record UpdateInstanceRequest(string? Name, string? Description, string? Config, bool? AiToolsEnabled = null);
}
