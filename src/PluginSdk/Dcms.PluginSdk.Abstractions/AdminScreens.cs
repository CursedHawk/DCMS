namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// A screen the plugin adds to the admin console. The manifest says it exists — its id, title,
/// who may open it and where it goes in the menu — so the platform can draw the menu and guard
/// the route without loading any plugin code; the plugin's admin UI module supplies the React
/// component under the same <see cref="Id"/> (<c>definePluginAdmin({ screens: { inbox: Inbox } })</c>).
///
/// <para>A <see cref="AdminScreenScope.Plugin"/> screen covers all of the tenant's instances
/// (the Forms inbox) and lives at <c>/app/{pluginId}/{id}</c>; an
/// <see cref="AdminScreenScope.Instance"/> screen belongs to one instance and is a tab of that
/// instance's page, <c>/plugins/{slug}/{id}</c>. Menu entries appear only while an instance of
/// the plugin is enabled.</para>
/// </summary>
/// <param name="Id">Kebab-case, unique within the plugin.</param>
/// <param name="Title">English; the menu label and page title.</param>
/// <param name="Permission">A bare action of the plugin's own or a full key; null means plugins:manage.</param>
/// <param name="IconName">A lucide icon name.</param>
/// <param name="Nav">Where it goes in the menu; null keeps it off the menu (reachable from the plugin's page).</param>
/// <param name="Titles">The title in other languages, by language code (<c>{ ["cs"] = "Doručené" }</c>).</param>
public sealed record AdminScreen(
    string Id,
    string Title,
    AdminScreenScope Scope = AdminScreenScope.Plugin,
    string? Permission = null,
    string? IconName = null,
    AdminNavPlacement? Nav = null,
    IReadOnlyDictionary<string, string>? Titles = null,
    string? Description = null);

public enum AdminScreenScope
{
    /// <summary>One screen for the whole tenant, across every instance of the plugin.</summary>
    Plugin,

    /// <summary>A screen per instance, shown on that instance's page.</summary>
    Instance,
}

/// <summary>A menu entry for a screen.</summary>
/// <param name="Group">The sidebar section: <c>main</c> (daily work), <c>build</c> (sites and content) or <c>admin</c>.</param>
/// <param name="Order">Position within the section; platform entries use 0–99.</param>
public sealed record AdminNavPlacement(string Group = "main", int Order = 100);
