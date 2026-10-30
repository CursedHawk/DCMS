namespace Dcms.Plugins.Functions.Api;

/// <summary>
/// The Functions plugin's permission keys (ADR 0029). What one function may reach is not here:
/// it declares that in its own <c>uses</c>, and whoever deploys it must hold what that implies.
/// </summary>
public static class FunctionsPermissions
{
    public const string PluginId = "functions";

    public const string Read = $"plugin:{PluginId}:read";
    public const string Edit = $"plugin:{PluginId}:edit";
    public const string Deploy = $"plugin:{PluginId}:deploy";
    public const string Logs = $"plugin:{PluginId}:logs";
    public const string Keys = $"plugin:{PluginId}:keys";
    public const string Secrets = $"plugin:{PluginId}:secrets";
}
