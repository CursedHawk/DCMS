namespace Dcms.Shared.Data.Cms;

/// <summary>
/// One configured, enable-able instance of a plugin for a tenant. Multi-instance
/// plugins have several rows (distinct slugs); the slug is the route segment and
/// the description flows into the generated OpenAPI document.
/// </summary>
public sealed class PluginInstance : TenantEntity
{
    public string PluginId { get; set; } = string.Empty;
    public string PluginVersion { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Plugin configuration as JSON (validated against the manifest schema).</summary>
    public string ConfigJson { get; set; } = "{}";

    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
