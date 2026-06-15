namespace Dcms.PluginSdk.Abstractions;

public sealed record PluginManifest(
    string Id,                          // stable kebab-case, e.g. "image-gallery"
    string Name,
    string Version,                     // semver; stored on instances for config migration
    string Description,                 // base description for OpenAPI + admin UI
    bool AllowMultipleInstances,
    string ConfigJsonSchema,            // JSON Schema driving the admin config form
    IReadOnlyList<PermissionDefinition> Permissions,
    IReadOnlyList<ContentTypeDefinition> ContentTypes,
    IReadOnlyList<PluginDependency> Dependencies)
{
    public static PluginManifest Create(
        string id,
        string name,
        string description,
        bool allowMultipleInstances,
        string configJsonSchema = "{}",
        IReadOnlyList<PermissionDefinition>? permissions = null,
        IReadOnlyList<ContentTypeDefinition>? contentTypes = null,
        IReadOnlyList<PluginDependency>? dependencies = null)
        => new(
            id,
            name,
            "1.0.0",
            description,
            allowMultipleInstances,
            configJsonSchema,
            permissions ?? [],
            contentTypes ?? [],
            dependencies ?? []);
}

/// <summary>Effective permission key: plugin:{pluginId}:{Action}.</summary>
public sealed record PermissionDefinition(string Action, string DisplayName);

/// <summary>Declares a dependency on another plugin (e.g. Articles → ImageGallery).</summary>
public sealed record PluginDependency(string PluginId, bool Optional);
