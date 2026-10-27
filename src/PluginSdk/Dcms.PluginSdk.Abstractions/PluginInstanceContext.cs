using System.Text.Json;

namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// One enabled, configured instance of a plugin for a tenant. Multi-instance
/// plugins get one context per instance, each with its own slug and
/// admin-authored description.
/// </summary>
public sealed record PluginInstanceContext(
    Guid InstanceId,
    Guid TenantId,
    string PluginId,
    string Slug,
    string Name,
    string Description,
    JsonDocument Config,
    // The tenant opted this instance's contract operations in to AI agents.
    bool AiToolsEnabled = false)
{
    private readonly Guid _environmentId;

    /// <summary>The environment of the tenant the instance belongs to (ADR 0028); production, the tenant's id, when not given.</summary>
    public Guid EnvironmentId
    {
        get => _environmentId == Guid.Empty ? TenantId : _environmentId;
        init => _environmentId = value;
    }
}
