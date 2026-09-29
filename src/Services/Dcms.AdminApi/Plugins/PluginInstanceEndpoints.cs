using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime;
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

        app.MapPost("/api/admin/plugins/instances", async (
            CreateInstanceRequest body, IPluginCatalog catalog, PluginRegistry registry, PluginConfigValidator validator,
            CmsDbContext db, ITenantContext tenant, IEventPublisher events, DcmsMetrics metrics,
            CancellationToken ct) =>
        {
            var manifest = catalog.Find(body.PluginId);
            if (manifest is null)
            {
                return Results.BadRequest(new { error = "Unknown plugin." });
            }
            if (PluginInstanceSlugs.Problem(body.Slug) is { } slugProblem)
            {
                return Results.BadRequest(new { error = slugProblem });
            }
            if (await db.PluginInstances.AnyAsync(p => p.Slug == body.Slug, ct))
            {
                return Results.Conflict(new { error = "slug already in use." });
            }
            if (!manifest.AllowMultipleInstances &&
                await db.PluginInstances.AnyAsync(p => p.PluginId == body.PluginId, ct))
            {
                return Results.Conflict(new { error = "This plugin allows only a single instance." });
            }

            // A new instance is enabled, so it must not need a contract nothing here provides.
            if (await MissingAsync(registry, db, manifest.Id, ct) is { Count: > 0 } missing)
            {
                return MissingConflict(missing);
            }

            var config = body.Config ?? "{}";
            var (valid, errors) = validator.Validate(manifest.ConfigJsonSchema, config);
            if (!valid)
            {
                return Results.BadRequest(new { error = "Invalid configuration.", details = errors });
            }

            var instance = new PluginInstance
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId!.Value,
                PluginId = manifest.Id,
                PluginVersion = manifest.Version,
                Slug = body.Slug,
                Name = body.Name ?? manifest.Name,
                Description = body.Description ?? manifest.Description,
                ConfigJson = config,
            };
            db.PluginInstances.Add(instance);
            await db.SaveChangesAsync(ct);
            await PublishChange(events, metrics, instance, PluginInstanceChangeKind.Created, ct);

            return Results.Created($"/api/admin/plugins/instances/{instance.Id}", new { id = instance.Id });
        }).RequirePermission(PlatformPermissions.PluginsManage).WithAudit(AuditActions.PluginInstanceCreated, "plugin_instance");

        app.MapPut("/api/admin/plugins/instances/{id:guid}", async (
            Guid id, UpdateInstanceRequest body, IPluginCatalog catalog, PluginConfigValidator validator,
            CmsDbContext db, IEventPublisher events, DcmsMetrics metrics, CancellationToken ct) =>
        {
            var instance = await db.PluginInstances.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (instance is null)
            {
                return Results.NotFound();
            }
            var manifest = catalog.Find(instance.PluginId);
            if (manifest is not null && body.Config is not null)
            {
                var (valid, errors) = validator.Validate(manifest.ConfigJsonSchema, body.Config);
                if (!valid)
                {
                    return Results.BadRequest(new { error = "Invalid configuration.", details = errors });
                }
                instance.ConfigJson = body.Config;
            }
            instance.Name = body.Name ?? instance.Name;
            instance.Description = body.Description ?? instance.Description;
            instance.AiToolsEnabled = body.AiToolsEnabled ?? instance.AiToolsEnabled;
            instance.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await PublishChange(events, metrics, instance, PluginInstanceChangeKind.Updated, ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.PluginsManage).WithAudit(AuditActions.PluginInstanceUpdated, "plugin_instance");

        app.MapPost("/api/admin/plugins/instances/{id:guid}/{action}", async (
            Guid id, string action, CmsDbContext db, PluginRegistry registry, IEventPublisher events, DcmsMetrics metrics,
            CancellationToken ct) =>
        {
            if (action is not ("enable" or "disable"))
            {
                return Results.NotFound();
            }
            var instance = await db.PluginInstances.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (instance is null)
            {
                return Results.NotFound();
            }
            if (action == "enable" && !instance.Enabled
                && await MissingAsync(registry, db, instance.PluginId, ct) is { Count: > 0 } missing)
            {
                return MissingConflict(missing);
            }
            if (action == "disable" && instance.Enabled)
            {
                var remaining = await db.PluginInstances.AsNoTracking()
                    .Where(p => p.Enabled && p.Id != instance.Id)
                    .Select(p => new { p.PluginId, p.Slug })
                    .ToListAsync(ct);
                var dependents = PluginDependencies.Dependents(
                    registry, instance.PluginId, remaining.Select(r => (r.PluginId, r.Slug)).ToList());
                if (dependents.Count > 0)
                {
                    return Results.Conflict(new
                    {
                        error = "Other plugins require this one; disable them first: "
                                + string.Join(", ", dependents.Select(d => $"{d.Slug} ({d.PluginId})").Distinct()) + ".",
                        dependents = dependents.Select(d => new { d.PluginId, d.Slug, d.ContractId }),
                    });
                }
            }
            instance.Enabled = action == "enable";
            instance.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await PublishChange(events, metrics, instance,
                instance.Enabled ? PluginInstanceChangeKind.Enabled : PluginInstanceChangeKind.Disabled, ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.PluginsManage).WithAudit(AuditActions.PluginInstanceActioned, "plugin_instance");

        return app;
    }

    private static async Task<IReadOnlyList<MissingProvider>> MissingAsync(
        PluginRegistry registry, CmsDbContext db, string pluginId, CancellationToken ct)
    {
        var enabled = await db.PluginInstances.AsNoTracking()
            .Where(p => p.Enabled).Select(p => p.PluginId).Distinct().ToListAsync(ct);
        return PluginDependencies.Missing(registry, pluginId, enabled);
    }

    private static IResult MissingConflict(IReadOnlyList<MissingProvider> missing) => Results.Conflict(new
    {
        error = "This plugin requires plugins that are not enabled here; enable them first: "
                + string.Join(", ", missing.Select(m => m.ProviderPluginId).Distinct()) + ".",
        missing = missing.Select(m => new { m.ContractId, m.ProviderPluginId }),
    });

    /// <summary>
    /// The single place a plugin instance change leaves this service, which is why the counter
    /// lives here rather than at the three call sites: a fourth kind of change added later gets
    /// counted by construction instead of by remembering.
    /// </summary>
    private static Task PublishChange(IEventPublisher events, DcmsMetrics metrics, PluginInstance instance, PluginInstanceChangeKind kind, CancellationToken ct)
    {
        // Plugin id, not instance id: the catalogue is a fixed, small set, whereas instances
        // are created per tenant without bound.
        metrics.PluginInstanceChanged(instance.PluginId, kind.ToString());
        return events.PublishAsync(Subjects.PluginInstanceChanged,
            new PluginInstanceChanged(Guid.NewGuid(), DateTimeOffset.UtcNow, instance.TenantId, instance.Id, instance.PluginId, kind), ct).AsTask();
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
        dependencies = m.Dependencies.Select(d => new { d.PluginId, d.Optional }),
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
