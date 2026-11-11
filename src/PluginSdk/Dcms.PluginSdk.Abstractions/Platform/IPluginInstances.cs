using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.PluginSdk.Abstractions.Platform;

/// <param name="Requires">Plugins one of which must be enabled first, per contract this plugin needs.</param>
public sealed record AvailablePlugin(
    string Id, string Name, string? Summary, string Category, bool AllowMultipleInstances, bool HasSettings, IReadOnlyList<string> Requires);

/// <param name="AiToolsEnabled">Whether the agent may configure this instance; only a person switches it, in the instance's Settings.</param>
public sealed record InstalledPlugin(Guid Id, string PluginId, string Slug, string Name, string? Description, bool Enabled, bool AiToolsEnabled);

public sealed record PluginOverview(IReadOnlyList<AvailablePlugin> Available, IReadOnlyList<InstalledPlugin> Installed);

/// <param name="Instance">The instance's slug (or id).</param>
public sealed record PluginInstanceRef(string Instance);

/// <param name="ConfigSchema">The JSON Schema the config must satisfy.</param>
public sealed record PluginSettings(InstalledPlugin Instance, JsonElement ConfigSchema, JsonElement Config);

/// <param name="Config">The whole new configuration (it replaces the stored one); null keeps it. Get the settings first and change only what is asked.</param>
public sealed record PluginSettingsUpdate(string Instance, string? Name = null, string? Description = null, JsonElement? Config = null);

/// <param name="Slug">Lowercase letters, digits and hyphens: the instance's address (<c>/api/{slug}</c>) and its tools' prefix.</param>
public sealed record PluginInstall(string PluginId, string Slug, string? Name = null, string? Description = null, JsonElement? Config = null);

public sealed record PluginEnabledChange(string Instance, bool Enabled);

/// <summary>
/// The tenant's plugins for the admin console and the assistant: what can be installed, what is,
/// and each instance's settings. The same rules as the console's routes — schema validation,
/// required plugins, the deployment-managed refusal — because both go through one service.
///
/// <para><b>On the AI plane an instance is configured only once a person has turned on its AI
/// tools</b>; no operation here changes that switch. Admin-api only.</para>
/// </summary>
[DcmsContract("dcms.plugins", 1, Description = "Install, enable or disable the tenant's plugins and change their settings.")]
public interface IPluginInstances
{
    [Operation(OpRisk.Read, Permission = "plugins:manage", Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Every plugin that can be installed, and every installed instance with whether it is enabled and whether its AI tools are on " +
                      "(only then can its settings be changed from here).")]
    Task<PluginOverview> ListAsync(CancellationToken ct);

    [Operation(OpRisk.Read, Permission = "plugins:manage", Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "An instance's settings: its configuration and the JSON Schema it must satisfy.")]
    Task<PluginSettings> GetSettingsAsync(PluginInstanceRef input, CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = "plugins:manage", Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Change an instance's name, description or configuration. The configuration given replaces the stored one as a whole: " +
                      "read it with GetSettings first and send it back with only the asked-for changes.")]
    Task<PluginSettings> UpdateSettingsAsync(PluginSettingsUpdate input, CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = "plugins:manage", Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Install a plugin as a new, enabled instance. Its AI tools start off: ask the operator to turn them on in its Settings " +
                      "before configuring it from here.")]
    Task<InstalledPlugin> InstallAsync(PluginInstall input, CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = "plugins:manage", Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Enable or disable an instance. Disabling takes its site API, pages and tools offline.")]
    Task<InstalledPlugin> SetEnabledAsync(PluginEnabledChange input, CancellationToken ct);
}
