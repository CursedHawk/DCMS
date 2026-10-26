namespace Dcms.Shared.Data.Tenancy;

/// <summary>
/// A migration as applied in one environment (ADR 0026): a named, ordered set of configuration
/// changes, cut in <see cref="OriginTenantId"/> and applied — in the same order and under the same
/// <see cref="MigrationId"/> — in every environment downstream. The environment it was cut in holds
/// a row too, so an environment's rows are its whole history, which is what the next environment
/// reads to learn what is pending.
///
/// <para>Each row carries the migration itself (name, changes), not a pointer: history, review and
/// rollback keep working after the environment it came from is deleted.</para>
/// </summary>
public sealed class EnvironmentMigration : TenantEntity
{
    public Guid MigrationId { get; set; }
    public Guid OriginTenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? AuthorUserId { get; set; }
    public string? AuthorEmail { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The changes, in order: <c>[{kind, key, label, op, before, after}]</c>.</summary>
    public string ChangesJson { get; set; } = "[]";

    /// <summary>Order of application in this environment.</summary>
    public long Order { get; set; }
    public DateTimeOffset AppliedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? AppliedBy { get; set; }
    /// <summary>What the reviewer chose for each conflicting change: <c>{"kind/key": "overwrite"|"skip"}</c>.</summary>
    public string ResolutionsJson { get; set; } = "{}";
    public DateTimeOffset? RolledBackAt { get; set; }
}

/// <summary>
/// An environment's configuration as of its last cut or apply: what "unpublished changes" are
/// measured against. One row per environment; <c>{kind: {key: value}}</c>.
/// </summary>
public sealed class EnvironmentBaseline : TenantEntity
{
    public string ItemsJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Who owns a kind of configuration in an environment, or one item of it (ADR 0026): edited
/// here, written by migrations, or both. An item row overrides its kind's; no row is
/// <see cref="EnvironmentOwnership.Mixed"/>.
/// </summary>
public sealed class EnvironmentPolicy : TenantEntity
{
    public string Kind { get; set; } = string.Empty;
    public string? ItemKey { get; set; }
    public EnvironmentOwnership Mode { get; set; }
}

public enum EnvironmentOwnership
{
    /// <summary>Edited here, and migrations apply to it too.</summary>
    Mixed,
    /// <summary>Edited here; migrations skip it.</summary>
    Local,
    /// <summary>Only migrations write it; they also delete what the source removed.</summary>
    Managed,
}

public static class EnvironmentPolicies
{
    /// <summary>The item's own policy, else its kind's, else <see cref="EnvironmentOwnership.Mixed"/>.</summary>
    public static EnvironmentOwnership Ownership(IEnumerable<EnvironmentPolicy> policies, string kind, string key)
    {
        var mine = policies.Where(p => p.Kind == kind).ToList();
        return (mine.FirstOrDefault(p => p.ItemKey == key) ?? mine.FirstOrDefault(p => p.ItemKey == null))?.Mode
               ?? EnvironmentOwnership.Mixed;
    }

    /// <summary>Whether <paramref name="kind"/>'s item <paramref name="key"/> is left to deployments in this tenant.</summary>
    public static async Task<bool> IsManagedAsync(TenancyDbContext db, Guid tenantId, string kind, string key, CancellationToken ct)
    {
        // rls: names the tenant; the caller is in its scope.
        var policies = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters(db.EnvironmentPolicies)
                .Where(p => p.TenantId == tenantId && p.Kind == kind), ct);
        return Ownership(policies, kind, key) == EnvironmentOwnership.Managed;
    }
}

/// <summary>
/// One environment using another's resource (ADR 0026), set up in the consuming environment
/// (<see cref="TenantEntity.TenantId"/>) and only ever within one workspace.
/// <list type="bullet">
/// <item><c>app</c>: the consumer's <c>/api/{TargetKey}</c> is served by the target's app of that
/// slug — its published content, its Dynamic Apps data — read only, or read and write.</item>
/// </list>
/// </summary>
public sealed class EnvironmentLink : TenantEntity
{
    public Guid TargetTenantId { get; set; }
    public string Kind { get; set; } = EnvironmentLinkKinds.App;
    public string? TargetKey { get; set; }
    public EnvironmentLinkAccess Access { get; set; } = EnvironmentLinkAccess.Read;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class EnvironmentLinkKinds
{
    public const string App = "app";

    /// <summary>The cached links of one consuming environment: read by content-api, cleared by admin-api on any change.</summary>
    public static string CacheKey(Guid tenantId) => $"t:{tenantId}:environment-links";
}

/// <summary>A link as content-api caches it.</summary>
public sealed record EnvironmentLinkEntry(string Kind, string? TargetKey, Guid TargetTenantId, EnvironmentLinkAccess Access);

public enum EnvironmentLinkAccess
{
    Read,
    ReadWrite,
}
