using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
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
                })
                .ToListAsync(ct);
            return Results.Ok(instances);
        }).RequirePermission(PlatformPermissions.PluginsManage);

        app.MapPost("/api/admin/plugins/instances", async (
            CreateInstanceRequest body, IPluginCatalog catalog, PluginConfigValidator validator,
            CmsDbContext db, ITenantContext tenant, IEventPublisher events, CancellationToken ct) =>
        {
            var manifest = catalog.Find(body.PluginId);
            if (manifest is null)
            {
                return Results.BadRequest(new { error = "Unknown plugin." });
            }
            if (string.IsNullOrWhiteSpace(body.Slug) || !IsSlug(body.Slug))
            {
                return Results.BadRequest(new { error = "slug must be kebab-case." });
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
            await PublishChange(events, instance, PluginInstanceChangeKind.Created, ct);

            return Results.Created($"/api/admin/plugins/instances/{instance.Id}", new { id = instance.Id });
        }).RequirePermission(PlatformPermissions.PluginsManage);

        app.MapPut("/api/admin/plugins/instances/{id:guid}", async (
            Guid id, UpdateInstanceRequest body, IPluginCatalog catalog, PluginConfigValidator validator,
            CmsDbContext db, IEventPublisher events, CancellationToken ct) =>
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
            instance.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await PublishChange(events, instance, PluginInstanceChangeKind.Updated, ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.PluginsManage);

        app.MapPost("/api/admin/plugins/instances/{id:guid}/{action}", async (
            Guid id, string action, CmsDbContext db, IEventPublisher events, CancellationToken ct) =>
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
            instance.Enabled = action == "enable";
            instance.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await PublishChange(events, instance,
                instance.Enabled ? PluginInstanceChangeKind.Enabled : PluginInstanceChangeKind.Disabled, ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.PluginsManage);

        return app;
    }

    private static Task PublishChange(IEventPublisher events, PluginInstance instance, PluginInstanceChangeKind kind, CancellationToken ct)
        => events.PublishAsync(Subjects.PluginInstanceChanged,
            new PluginInstanceChanged(Guid.NewGuid(), DateTimeOffset.UtcNow, instance.TenantId, instance.Id, instance.PluginId, kind), ct).AsTask();

    private static object ToManifestDto(PluginManifest m) => new
    {
        id = m.Id,
        name = m.Name,
        version = m.Version,
        description = m.Description,
        allowMultipleInstances = m.AllowMultipleInstances,
        configJsonSchema = m.ConfigJsonSchema,
        permissions = m.Permissions.Select(p => new { p.Action, p.DisplayName }),
        contentTypes = m.ContentTypes.Select(t => new { t.Name, t.Searchable, t.SlugField }),
    };

    private static bool IsSlug(string value) =>
        value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
        && !value.StartsWith('-') && !value.EndsWith('-');

    private sealed record CreateInstanceRequest(string PluginId, string Slug, string? Name, string? Description, string? Config);
    private sealed record UpdateInstanceRequest(string? Name, string? Description, string? Config);
}
