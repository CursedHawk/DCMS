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

    /// <summary>
    /// Where a migration applies this kind among the others, lowest first. The host's own kinds
    /// sit below 100 — roles, then plugin instances — so a plugin's kind (100 by default) finds
    /// the instance it configures already there.
    /// </summary>
    int Order => 100;

    /// <summary>
    /// What applying <paramref name="value"/> needs its applier to hold beyond
    /// <see cref="Permission"/>: an item that acts with its publisher's authority — a Dynamic Apps
    /// flow using a permissioned action — must be deployed by someone who holds it. The engine
    /// checks before anything is written; <see cref="ApplyAsync"/> may then take them as vouched.
    /// </summary>
    Task<IReadOnlyCollection<string>> PermissionsForAsync(Guid tenantId, string key, JsonElement value, CancellationToken ct) =>
        Task.FromResult<IReadOnlyCollection<string>>([]);

    /// <summary>Every item of this kind the tenant has.</summary>
    Task<IReadOnlyList<EnvironmentConfigItem>> ExportAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Makes the tenant's item <paramref name="key"/> equal <paramref name="value"/>, creating it
    /// when missing; a null value deletes it. Must be idempotent: a migration interrupted halfway
    /// is applied again from the start.
    /// </summary>
    Task ApplyAsync(Guid tenantId, string key, JsonElement? value, EnvironmentApplyContext context, CancellationToken ct);
}

/// <summary>Who an item is being applied for.</summary>
/// <param name="Held">
/// The permissions the deployer was checked to hold in the receiving environment; null when no
/// person is behind the write (a platform administrator, or a new environment copying its source).
/// What an item vouches for must come from here, never be worked out afresh at write time: an
/// earlier write in the same deployment can change what that would say.
/// </param>
public sealed record EnvironmentApplyContext(IReadOnlySet<string>? Held);

/// <param name="Key">The natural key, the same in every environment.</param>
/// <param name="Value">What moves between environments.</param>
public sealed record EnvironmentConfigItem(string Key, JsonElement Value)
{
    /// <summary>What a person calls it, for the review screen; the key when absent.</summary>
    public string? Label { get; init; }
}

/// <summary>
/// An item a kind cannot apply in this environment as it stands — what it depends on is missing,
/// or the value breaks this environment's rules. Fails the deployment with
/// <paramref name="reason"/> rather than leaving the item silently different from what was reviewed.
/// </summary>
public sealed class EnvironmentConfigException(string reason) : Exception(reason);
