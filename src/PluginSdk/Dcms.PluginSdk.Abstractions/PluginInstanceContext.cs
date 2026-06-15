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
    JsonDocument Config);
