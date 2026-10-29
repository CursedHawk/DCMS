namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// The tables of an instance that a <c>tables</c> link from another environment may name, with
/// read, create, update or delete each (ADR 0028). Provided, in admin-api, by the plugin whose data
/// they are; a plugin without one cannot be linked to as tables at all.
/// </summary>
public interface IEnvironmentLinkTables
{
    /// <summary>The plugin whose instances these are.</summary>
    string PluginId { get; }

    /// <summary>
    /// The instance's tables as they are now, by name; empty when it has none yet. Called inside
    /// the instance's environment.
    /// </summary>
    Task<IReadOnlyList<string>> TablesAsync(Guid tenantId, Guid instanceId, CancellationToken ct);
}
