using Dcms.AdminApi.Tenancy;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Caching;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Plugins;

/// <summary>
/// A refusal of a plugin instance change: the status and body the console's routes have always
/// answered with, and a sentence for the contract path (<c>dcms.plugins@1</c>).
/// </summary>
public sealed class PluginInstanceRefusal(int status, string message, object body) : Exception(message)
{
    public int Status => status;
    public object Body => body;

    public static PluginInstanceRefusal BadRequest(string error, object? details = null) =>
        new(StatusCodes.Status400BadRequest, details is null ? error : $"{error} {string.Join(" ", details as IEnumerable<string> ?? [])}".Trim(),
            details is null ? new { error } : new { error, details });

    public static PluginInstanceRefusal Conflict(string error, object? extra = null) =>
        new(StatusCodes.Status409Conflict, error, extra ?? new { error });
}

/// <summary>
/// Installing, configuring and enabling plugin instances: one code path for the console's routes
/// and the assistant's <c>dcms.plugins@1</c>. Refusals are <see cref="PluginInstanceRefusal"/>;
/// a missing instance is null.
///
/// <para>The deployment-managed check repeats the routes' middleware for the contract path, which
/// has no route of its own to mark.</para>
/// </summary>
public sealed class PluginInstanceService(
    IPluginCatalog catalog, PluginRegistry registry, PluginConfigValidator validator, CmsDbContext db, TenancyDbContext tenancy,
    ITenantContext tenant, IEventPublisher events, ICacheService cache, DcmsMetrics metrics)
{
    public const string ManagedKind = "plugins";

    public async Task<PluginInstance> CreateAsync(string pluginId, string slug, string? name, string? description, string? config, CancellationToken ct)
    {
        var manifest = catalog.Find(pluginId) ?? throw PluginInstanceRefusal.BadRequest("Unknown plugin.");
        if (PluginInstanceSlugs.Problem(slug) is { } slugProblem)
        {
            throw PluginInstanceRefusal.BadRequest(slugProblem);
        }
        if (catalog.Manifests.FirstOrDefault(m => m.ReservedSlugs?.Contains(slug) == true) is { } owner)
        {
            throw PluginInstanceRefusal.BadRequest($"slug '{slug}' is reserved: the {owner.Name} plugin's routes use it.");
        }
        await EnsureNotManagedAsync(slug, ct);
        if (await db.PluginInstances.AnyAsync(p => p.Slug == slug, ct))
        {
            throw PluginInstanceRefusal.Conflict("slug already in use.");
        }
        if (await EnvironmentLinkRules.AppLinkedAsync(tenancy, tenant.TenantId!.Value, slug, ct))
        {
            throw PluginInstanceRefusal.Conflict("Another environment's app is linked at this address.");
        }
        if (!manifest.AllowMultipleInstances && await db.PluginInstances.AnyAsync(p => p.PluginId == pluginId, ct))
        {
            throw PluginInstanceRefusal.Conflict("This plugin allows only a single instance.");
        }

        // A new instance is enabled, so it must not need a contract nothing here provides.
        if (await MissingAsync(manifest.Id, ct) is { Count: > 0 } missing)
        {
            throw MissingConflict(missing);
        }

        config ??= "{}";
        Validate(manifest, config);

        var instance = new PluginInstance
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId!.Value,
            PluginId = manifest.Id,
            PluginVersion = manifest.Version,
            Slug = slug,
            Name = name ?? manifest.Name,
            Description = description ?? manifest.Description,
            ConfigJson = config,
        };
        var firstOfPlugin = !await db.PluginInstances.AnyAsync(p => p.PluginId == manifest.Id, ct);
        db.PluginInstances.Add(instance);
        await db.SaveChangesAsync(ct);
        if (firstOfPlugin)
        {
            await PluginDefaultGrants.ApplyAsync(tenancy, instance.TenantId, manifest, ct);
        }
        await PluginInstanceEndpoints.PublishChange(events, cache, metrics, instance, PluginInstanceChangeKind.Created, ct);
        return instance;
    }

    /// <param name="aiToolsEnabled">The console's switch. The contract path never passes it: the agent does not widen its own reach.</param>
    public async Task<PluginInstance?> UpdateAsync(Guid id, string? name, string? description, string? config, bool? aiToolsEnabled, CancellationToken ct)
    {
        var instance = await db.PluginInstances.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (instance is null)
        {
            return null;
        }
        await EnsureNotManagedAsync(instance.Slug, ct);
        if (catalog.Find(instance.PluginId) is { } manifest && config is not null)
        {
            Validate(manifest, config);
            instance.ConfigJson = config;
        }
        instance.Name = name ?? instance.Name;
        instance.Description = description ?? instance.Description;
        instance.AiToolsEnabled = aiToolsEnabled ?? instance.AiToolsEnabled;
        instance.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await PluginInstanceEndpoints.PublishChange(events, cache, metrics, instance, PluginInstanceChangeKind.Updated, ct);
        return instance;
    }

    public async Task<PluginInstance?> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        var instance = await db.PluginInstances.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (instance is null)
        {
            return null;
        }
        await EnsureNotManagedAsync(instance.Slug, ct);
        if (enabled && !instance.Enabled && await MissingAsync(instance.PluginId, ct) is { Count: > 0 } missing)
        {
            throw MissingConflict(missing);
        }
        if (!enabled && instance.Enabled)
        {
            var remaining = await db.PluginInstances.AsNoTracking()
                .Where(p => p.Enabled && p.Id != instance.Id)
                .Select(p => new { p.PluginId, p.Slug })
                .ToListAsync(ct);
            var dependents = PluginDependencies.Dependents(registry, instance.PluginId, remaining.Select(r => (r.PluginId, r.Slug)).ToList());
            if (dependents.Count > 0)
            {
                var error = "Other plugins require this one; disable them first: "
                            + string.Join(", ", dependents.Select(d => $"{d.Slug} ({d.PluginId})").Distinct()) + ".";
                throw PluginInstanceRefusal.Conflict(error, new { error, dependents = dependents.Select(d => new { d.PluginId, d.Slug, d.ContractId }) });
            }
        }
        instance.Enabled = enabled;
        instance.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await PluginInstanceEndpoints.PublishChange(events, cache, metrics, instance,
            instance.Enabled ? PluginInstanceChangeKind.Enabled : PluginInstanceChangeKind.Disabled, ct);
        return instance;
    }

    private void Validate(PluginManifest manifest, string config)
    {
        var (valid, errors) = validator.Validate(manifest.ConfigJsonSchema, config);
        if (!valid)
        {
            throw PluginInstanceRefusal.BadRequest("Invalid configuration.", errors);
        }
    }

    private async Task EnsureNotManagedAsync(string slug, CancellationToken ct)
    {
        if (tenant.TenantId is { } tenantId
            && await EnvironmentPolicies.IsManagedAsync(tenancy, tenantId, tenant.EnvironmentId ?? tenantId, ManagedKind, slug, ct))
        {
            throw PluginInstanceRefusal.Conflict(
                "This app is managed by deployments in this environment: change it in the environment that deploys here, and deploy it.",
                new { error = "This is managed by deployments. Change it in the environment that deploys here.", code = "environment_managed", kind = ManagedKind });
        }
    }

    private async Task<IReadOnlyList<MissingProvider>> MissingAsync(string pluginId, CancellationToken ct)
    {
        var enabled = await db.PluginInstances.AsNoTracking()
            .Where(p => p.Enabled).Select(p => p.PluginId).Distinct().ToListAsync(ct);
        return PluginDependencies.Missing(registry, pluginId, enabled);
    }

    private static PluginInstanceRefusal MissingConflict(IReadOnlyList<MissingProvider> missing)
    {
        var error = "This plugin requires plugins that are not enabled here; enable them first: "
                    + string.Join(", ", missing.Select(m => string.Join(" or ", m.ProviderPluginIds)).Distinct()) + ".";
        return PluginInstanceRefusal.Conflict(error, new { error, missing = missing.Select(m => new { m.ContractId, m.ProviderPluginIds }) });
    }
}
