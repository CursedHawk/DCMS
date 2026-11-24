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
            .RequireAuthorization()
            .Cli("plugins.catalog", "Every plugin this platform offers, with its settings schema and permissions.", CliRisk.Read);

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
        }).RequirePermission(PlatformPermissions.PluginsManage)
          .Cli("plugins.list", "The plugin instances (apps) installed in this workspace, enabled or not.", CliRisk.Read);

        // Install, configure, enable: PluginInstanceService, shared with dcms.plugins@1.
        app.MapPost("/api/admin/plugins/instances", (CreateInstanceRequest body, PluginInstanceService plugins, CancellationToken ct) =>
            Run(async () =>
            {
                var instance = await plugins.CreateAsync(body.PluginId, body.Slug, body.Name, body.Description, body.Config, ct);
                return Results.Created($"/api/admin/plugins/instances/{instance.Id}", new { id = instance.Id });
            })).RequirePermission(PlatformPermissions.PluginsManage).WithAudit(AuditActions.PluginInstanceCreated, "plugin_instance")
            .ManagedByDeployments("plugins")
            .Cli("plugins.install", "Installs a plugin as a new app. Agents use the dcms.plugins contract, which keeps AI settings to people.", CliRisk.Dangerous, humanOnly: true);

        app.MapPut("/api/admin/plugins/instances/{id:guid}", (Guid id, UpdateInstanceRequest body, PluginInstanceService plugins, CancellationToken ct) =>
            Run(async () => await plugins.UpdateAsync(id, body.Name, body.Description, body.Config, body.AiToolsEnabled, ct) is null
                ? Results.NotFound()
                : Results.NoContent()))
            .RequirePermission(PlatformPermissions.PluginsManage).WithAudit(AuditActions.PluginInstanceUpdated, "plugin_instance")
            .ManagedByDeployments("plugins")
            .Cli("plugins.update", "Changes an app's name, description, settings or AI tools switch. Agents use the dcms.plugins contract, which never flips the AI switch.", CliRisk.Dangerous, humanOnly: true);

        app.MapPost("/api/admin/plugins/instances/{id:guid}/{action}", (Guid id, string action, PluginInstanceService plugins, CancellationToken ct) =>
            action is not ("enable" or "disable")
                ? Task.FromResult(Results.NotFound())
                : Run(async () => await plugins.SetEnabledAsync(id, action == "enable", ct) is null ? Results.NotFound() : Results.NoContent()))
            .RequirePermission(PlatformPermissions.PluginsManage).WithAudit(AuditActions.PluginInstanceActioned, "plugin_instance")
            .ManagedByDeployments("plugins")
            .Cli("plugins.action", "Enables or disables an app (action: enable, disable). Agents use the dcms.plugins contract.", CliRisk.Dangerous, humanOnly: true);

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
