using Microsoft.Extensions.Configuration;

namespace Dcms.PluginSdk.Abstractions;

/// <summary>Which host is running plugins; a few platform contracts and routes differ between them.</summary>
public enum PluginPlane
{
    /// <summary>content-api: public, internet-facing.</summary>
    Site,

    /// <summary>admin-api: authenticated members, jobs and event handlers.</summary>
    Admin,
}

/// <summary>
/// The host a plugin is being configured in. <see cref="Configuration"/> is the host's whole
/// configuration (connection strings, service URLs); <see cref="SettingsFor"/> is the operator's
/// section for one plugin, <c>Plugins:{id}</c> — where deployment-wide plugin settings belong,
/// as opposed to per-tenant instance config.
/// </summary>
public sealed record PluginHost(PluginPlane Plane, IConfiguration Configuration, string EnvironmentName = "Production")
{
    public bool IsSite => Plane == PluginPlane.Site;

    public bool IsAdmin => Plane == PluginPlane.Admin;

    /// <summary>
    /// Whether this process runs background work: hosted services, consumers, schedulers. True in
    /// jobs-worker only (ADR 0036); admin-api and content-api serve requests. A plugin registers
    /// its hosted services only where this is true, so they run on the workers and not beside
    /// every request.
    /// </summary>
    public bool RunsBackgroundWork { get; init; } = true;

    public bool IsProduction => string.Equals(EnvironmentName, "Production", StringComparison.OrdinalIgnoreCase);

    public bool IsDevelopment => string.Equals(EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);

    public IConfigurationSection SettingsFor(string pluginId) => Configuration.GetSection($"Plugins:{pluginId}");

    /// <summary>A host with no configuration, for tests and tools.</summary>
    public static PluginHost Empty(PluginPlane plane = PluginPlane.Site) =>
        new(plane, new ConfigurationBuilder().Build(), "Development");
}
