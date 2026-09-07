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
    IReadOnlyList<string> PublicConfigKeys,

    /*
     * Presentation, for the marketplace and the navigation menu.
     *
     * All optional and all defaulted, because every one of the eighteen shipped plugins goes
     * through `Create` — adding a required field here would be eighteen edits for metadata that
     * only changes how a plugin is *listed*.
     *
     * Absent values degrade honestly rather than blocking: no category means "Other", no icon
     * means the generic plugin glyph, no nav declaration means the plugin contributes no menu
     * entry, which is the correct default for one that has no page of its own.
     */

    /// <summary>Groups the plugin in the marketplace. Free text; unknown values sort into "Other".</summary>
    string? Category = null,
    /// <summary>One line for a marketplace card, where <see cref="Description"/> is a paragraph.</summary>
    string? Summary = null,
    /// <summary>A lucide icon name, resolved by the SPA. An unknown name falls back to the plugin glyph.</summary>
    string? IconName = null,
    IReadOnlyList<string>? Tags = null,
    /// <summary>
    /// A menu entry this plugin's enabled instances contribute, or null for a plugin with no
    /// page of its own — most of them, which surface through Content and the builder palette.
    /// </summary>
    PluginNavDeclaration? Nav = null)
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
        IReadOnlyList<string>? publicConfigKeys = null,
        string? category = null,
        string? summary = null,
        string? iconName = null,
        IReadOnlyList<string>? tags = null,
        PluginNavDeclaration? nav = null)
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
            publicConfigKeys ?? [],
            category,
            summary,
            iconName,
            tags ?? [],
            nav);
}

/// <summary>
/// A navigation entry contributed by a plugin's enabled instances.
///
/// <para>The route is a template: <c>{instanceSlug}</c> is replaced per instance, so one
/// declaration yields one menu entry per installed instance — "Image gallery · Homepage" and
/// "Image gallery · Press kit" rather than a single ambiguous "Image gallery".</para>
///
/// <para><see cref="Permission"/> is the key a member must hold to see the entry at all. It is
/// stated rather than inferred from the plugin's own permission list because a plugin can
/// declare several, and the one that gates its page is not always the first.</para>
/// </summary>
public sealed record PluginNavDeclaration(
    string LabelKey,
    string RouteTemplate,
    string Permission,
    string? IconName = null,
    /// <summary>Which sidebar group it joins. Unknown values fall into the plugin section.</summary>
    string Group = "plugins");

/// <summary>Effective permission key: plugin:{pluginId}:{Action}.</summary>
public sealed record PermissionDefinition(string Action, string DisplayName);

/// <summary>Declares a dependency on another plugin (e.g. Articles → ImageGallery).</summary>
public sealed record PluginDependency(string PluginId, bool Optional);
