using Dcms.PluginSdk.Abstractions.Contracts;

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
    PluginNavDeclaration? Nav = null,

    /*
     * Contracts (docs/adr/0016-plugin-contracts.md). What this plugin offers other plugins and
     * the platform surfaces, what it needs from them, which of their events it handles, and
     * which background jobs it runs. `Consumes` is the plugin's grant list: the runtime refuses
     * a contract that is not declared here.
     */
    IReadOnlyList<ContractProvision>? Provides = null,
    IReadOnlyList<ContractRequirement>? Consumes = null,
    IReadOnlyList<EventSubscription>? Subscribes = null,
    IReadOnlyList<JobDeclaration>? Jobs = null,
    /// <summary>Hooks of consumed contracts this plugin intercepts.</summary>
    IReadOnlyList<HookSubscription>? Intercepts = null,
    /// <summary>
    /// Members this plugin's instances get in the generated TypeScript site client that are
    /// served by a runtime helper of <c>@dcms/api-client</c> rather than by described operations.
    /// </summary>
    IReadOnlyList<ClientBinding>? ClientBindings = null,
    /// <summary>
    /// Instance slugs this plugin's own routes make unusable, because a host route of its
    /// (<c>/api/analytics/status</c>) would shadow <c>/api/{slug}/…</c> for that slug.
    /// </summary>
    IReadOnlyList<string>? ReservedSlugs = null,
    /// <summary>
    /// The plugin records what site visitors do (analytics, a tracking pixel). A published
    /// site asks for consent only when an enabled plugin says so.
    /// </summary>
    bool TracksVisitors = false)
{
    public static PluginManifest Create(
        string id,
        string name,
        string description,
        bool allowMultipleInstances,
        string configJsonSchema = "{}",
        IReadOnlyList<PermissionDefinition>? permissions = null,
        IReadOnlyList<ContentTypeDefinition>? contentTypes = null,
        IReadOnlyList<string>? publicConfigKeys = null,
        string? category = null,
        string? summary = null,
        string? iconName = null,
        IReadOnlyList<string>? tags = null,
        PluginNavDeclaration? nav = null,
        IReadOnlyList<ContractProvision>? provides = null,
        IReadOnlyList<ContractRequirement>? consumes = null,
        IReadOnlyList<EventSubscription>? subscribes = null,
        IReadOnlyList<JobDeclaration>? jobs = null,
        IReadOnlyList<HookSubscription>? intercepts = null,
        IReadOnlyList<ClientBinding>? clientBindings = null,
        IReadOnlyList<string>? reservedSlugs = null,
        bool tracksVisitors = false)
        => new(
            id,
            name,
            "1.0.0",
            description,
            allowMultipleInstances,
            configJsonSchema,
            permissions ?? [],
            contentTypes ?? [],
            publicConfigKeys ?? [],
            category,
            summary,
            iconName,
            tags ?? [],
            nav,
            provides ?? [],
            consumes ?? [],
            subscribes ?? [],
            jobs ?? [],
            intercepts ?? [],
            clientBindings ?? [],
            reservedSlugs ?? [],
            tracksVisitors);
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

/// <summary>
/// One member of an instance in the generated TypeScript site client, backed by a runtime helper.
/// Templates may use <c>{slug}</c> (the raw slug), <c>{slugLiteral}</c> (it as a TypeScript string
/// literal) and <c>{member}</c> (it as written after a dot).
/// </summary>
/// <param name="Path">Below the instance member, e.g. <c>["search"]</c> gives <c>api.{slug}.search</c>.</param>
/// <param name="Expression">The member's TypeScript value.</param>
/// <param name="RuntimeTypes">Types the expression uses that the runtime module exports.</param>
public sealed record ClientBinding(
    IReadOnlyList<string> Path,
    string Expression,
    string Usage,
    string Returns,
    string Route,
    string Description,
    IReadOnlyList<string>? RuntimeTypes = null);

/// <summary>Effective permission key: plugin:{pluginId}:{Action}.</summary>
public sealed record PermissionDefinition(string Action, string DisplayName);
