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
    IReadOnlyList<PluginDependency> Dependencies,
    // Config keys a public site may read via GET /api/{slug}/_config. This is an
    // allow-list rather than a flag on purpose: instance config is tenant-private
    // by default, so adding a credential to a plugin's schema later cannot make
    // it public by accident.
    IReadOnlyList<string> PublicConfigKeys)
{
    public static PluginManifest Create(
        string id,
        string name,
        string description,
        bool allowMultipleInstances,
        string configJsonSchema = "{}",
        IReadOnlyList<PermissionDefinition>? permissions = null,
        IReadOnlyList<ContentTypeDefinition>? contentTypes = null,
        IReadOnlyList<PluginDependency>? dependencies = null,
        IReadOnlyList<string>? publicConfigKeys = null)
        => new(
            id,
            name,
            "1.0.0",
            description,
            allowMultipleInstances,
            configJsonSchema,
            permissions ?? [],
            contentTypes ?? [],
            dependencies ?? [],
            publicConfigKeys ?? []);
}

/// <summary>Effective permission key: plugin:{pluginId}:{Action}.</summary>
public sealed record PermissionDefinition(string Action, string DisplayName);

/// <summary>Declares a dependency on another plugin (e.g. Articles → ImageGallery).</summary>
public sealed record PluginDependency(string PluginId, bool Optional);
