namespace Dcms.Plugins.DynamicApps.Api;

/// <summary>
/// The coarse permission keys of the Dynamic Apps plugin. Finer rules (which tables a role may
/// read, what the public site may see) live in each application's own security configuration,
/// not here: a tenant's tables are runtime data, and the permission catalogue is not.
/// </summary>
public static class DynamicAppsPermissions
{
    public const string PluginId = "dynamic-apps";

    public const string ModelRead = $"plugin:{PluginId}:model-read";
    public const string ModelWrite = $"plugin:{PluginId}:model-write";
    public const string Publish = $"plugin:{PluginId}:publish";
    public const string DataRead = $"plugin:{PluginId}:data-read";
    public const string DataWrite = $"plugin:{PluginId}:data-write";
    public const string DataDelete = $"plugin:{PluginId}:data-delete";
    public const string DataImport = $"plugin:{PluginId}:data-import";
    public const string FlowsRun = $"plugin:{PluginId}:flows-run";
}
