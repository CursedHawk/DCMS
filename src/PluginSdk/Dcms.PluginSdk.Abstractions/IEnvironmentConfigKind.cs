using System.Text.Json;

namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// One kind of configuration a workspace's environments move between them as migrations
/// (ADR 0026): sites, plugin instances, roles — and whatever a plugin adds, the way Dynamic Apps
/// adds its applications. Content and data are never configuration.
///
/// <para>Registered (scoped) in <see cref="IPlugin.ConfigureServices"/> on the admin plane, or by
/// the host for its own kinds. The engine calls it inside the named tenant's database scope; an
/// implementation still names the tenant explicitly in every query, because it reads and writes
/// tenants other than the request's.</para>
///
/// <para>An item is addressed by a natural key that is the same in every environment — a slug,
/// never a row id, since each environment's rows have their own. Its value is everything that
/// moves, and nothing that does not: no ids, no timestamps, no secrets, no per-environment
/// addresses. Two environments holding the same configuration must export equal values, because
/// equality is how the engine tells a change from no change and a conflict from none.</para>
/// </summary>
public interface IEnvironmentConfigKind
{
    /// <summary>Stable id, e.g. <c>roles</c>, <c>plugins</c>, <c>dynamic-apps</c>.</summary>
    string Kind { get; }

    /// <summary>
    /// The permission whoever applies a change of this kind must hold in the receiving environment
    /// — the one its own screens require. A deployment is not a way round them.
    /// </summary>
    string? Permission => null;

    /// <summary>Every item of this kind the tenant has.</summary>
    Task<IReadOnlyList<EnvironmentConfigItem>> ExportAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Makes the tenant's item <paramref name="key"/> equal <paramref name="value"/>, creating it
    /// when missing; a null value deletes it. Must be idempotent: a migration interrupted halfway
    /// is applied again from the start.
    /// </summary>
    Task ApplyAsync(Guid tenantId, string key, JsonElement? value, CancellationToken ct);
}

/// <param name="Key">The natural key, the same in every environment.</param>
/// <param name="Value">What moves between environments.</param>
public sealed record EnvironmentConfigItem(string Key, JsonElement Value)
{
    /// <summary>What a person calls it, for the review screen; the key when absent.</summary>
    public string? Label { get; init; }
}
