using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Data;

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
     * means the generic plugin glyph. Menu entries come from `AdminScreens` (below).
     */

    // <summary>Groups the plugin in the marketplace. Free text; unknown values sort into "Other".</summary>
    string? Category = null,
    // <summary>One line for a marketplace card, where <see cref="Description"/> is a paragraph.</summary>
    string? Summary = null,
    // <summary>A lucide icon name, resolved by the SPA. An unknown name falls back to the plugin glyph.</summary>
    string? IconName = null,
    IReadOnlyList<string>? Tags = null,

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
    // <summary>Hooks of consumed contracts this plugin intercepts.</summary>
    IReadOnlyList<HookSubscription>? Intercepts = null,
    // <summary>
    // Members this plugin's instances get in the generated TypeScript site client that are
    // served by a runtime helper of <c>@dcms/api-client</c> rather than by described operations.
    // </summary>
    IReadOnlyList<ClientBinding>? ClientBindings = null,
    // <summary>
    // Instance slugs this plugin's own routes make unusable, because a host route of its
    // (<c>/api/analytics/status</c>) would shadow <c>/api/{slug}/…</c> for that slug.
    // </summary>
    IReadOnlyList<string>? ReservedSlugs = null,
    // <summary>
    // The plugin records what site visitors do (analytics, a tracking pixel). A published
    // site asks for consent only when an enabled plugin says so.
    // </summary>
    bool TracksVisitors = false,
    // <summary>
    // Tables of this plugin's data the admin console shows and edits on the plugin's page. The
    // platform adds its own for data the plugin keeps in dcms.storage and dcms.blobs.
    // </summary>
    IReadOnlyList<DataSetDeclaration>? DataSets = null,
    // <summary>
    // Screens the plugin adds to the admin console, with their menu entries. The plugin's admin
    // UI module (see docs/plugins.md) supplies the components.
    // </summary>
    IReadOnlyList<AdminScreen>? AdminScreens = null)
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
        IReadOnlyList<ContractProvision>? provides = null,
        IReadOnlyList<ContractRequirement>? consumes = null,
        IReadOnlyList<EventSubscription>? subscribes = null,
        IReadOnlyList<JobDeclaration>? jobs = null,
        IReadOnlyList<HookSubscription>? intercepts = null,
        IReadOnlyList<ClientBinding>? clientBindings = null,
        IReadOnlyList<string>? reservedSlugs = null,
        bool tracksVisitors = false,
        IReadOnlyList<DataSetDeclaration>? dataSets = null,
        IReadOnlyList<AdminScreen>? adminScreens = null)
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
            provides ?? [],
            consumes ?? [],
            subscribes ?? [],
            jobs ?? [],
            intercepts ?? [],
            clientBindings ?? [],
            reservedSlugs ?? [],
            tracksVisitors,
            dataSets ?? [],
            adminScreens ?? []);
}

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

/// <summary>
/// A permission the plugin adds to the platform. Its key is <c>plugin:{pluginId}:{Action}</c>;
/// anywhere the manifest or a route names a permission, the bare <c>Action</c> means this key.
/// Tenants grant it to roles in the role editor, where <see cref="Description"/> says what it
/// lets a member do. Owners always hold every plugin permission.
/// </summary>
/// <param name="Action">Kebab-case, unique within the plugin: <c>read</c>, <c>moderate</c>.</param>
/// <param name="GrantToMembers">
/// Granted to the built-in Member role when a tenant installs its first instance of the plugin.
/// A default, not a rule: the tenant can take it away again.
/// </param>
public sealed record PermissionDefinition(
    string Action,
    string DisplayName,
    string? Description = null,
    bool GrantToMembers = false);

/// <summary>Permission keys of a plugin, for code that has a plugin id and a manifest reference.</summary>
public static class PluginPermissions
{
    /// <summary>The key a manifest or route reference means: a bare action is the plugin's own, anything with ':' is a full key.</summary>
    public static string Resolve(string pluginId, string permission) =>
        permission.Contains(':') ? permission : $"plugin:{pluginId}:{permission}";
}
