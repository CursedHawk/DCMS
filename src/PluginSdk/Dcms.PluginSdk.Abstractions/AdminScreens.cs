namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// A screen the plugin adds to the admin console. The manifest says it exists — its id, title,
/// who may open it and where it goes in the menu — so the platform can draw the menu and guard
/// the route without loading any plugin code; the plugin's admin UI module supplies the React
/// component under the same <see cref="Id"/> (<c>definePluginAdmin({ screens: { inbox: Inbox } })</c>).
///
/// <para>Every enabled instance is an app in the console's menu, at <c>/apps/{slug}</c>: a frame
/// whose sub-navigation lists the plugin's <see cref="AdminScreenSection.Work"/> screens, then an
/// Admin group with its <see cref="AdminScreenSection.Admin"/> screens beside the platform's own
/// pages (data, integration, activity). A screen lives at <c>/apps/{slug}/{id}</c>. A
/// <see cref="AdminScreenScope.Plugin"/> screen covers all of the tenant's instances (the Forms
/// inbox) and is shown in each of them; an <see cref="AdminScreenScope.Instance"/> screen belongs
/// to one instance.</para>
///
/// <para>The screen with id <see cref="SettingsId"/> is the instance's settings: every plugin
/// provides one, in the Admin group (<c>schemaSettings()</c> from <c>@dcms/plugin-ui</c> renders
/// the config schema's form).</para>
/// </summary>
/// <param name="Id">Kebab-case, unique within the plugin.</param>
/// <param name="Title">English; the menu label and page title.</param>
/// <param name="Permission">A bare action of the plugin's own or a full key; null means plugins:manage.</param>
/// <param name="IconName">A lucide icon name.</param>
/// <param name="Nav">No longer read: every enabled instance is in the menu, with its work screens under it.
/// Kept so plugins built against an older SDK still load.</param>
/// <param name="Titles">The title in other languages, by language code (<c>{ ["cs"] = "Doručené" }</c>).</param>
public sealed record AdminScreen(
    string Id,
    string Title,
    AdminScreenScope Scope = AdminScreenScope.Plugin,
    string? Permission = null,
    string? IconName = null,
    AdminNavPlacement? Nav = null,
    IReadOnlyDictionary<string, string>? Titles = null,
    string? Description = null)
{
    /// <summary>
    /// Its group in the app's sub-navigation. An <c>init</c> property rather than a parameter:
    /// installed plugins are compiled against this constructor.
    /// </summary>
    public AdminScreenSection Section { get; init; } = AdminScreenSection.Work;

    /// <summary>The id of the screen that is an instance's settings.</summary>
    public const string SettingsId = "settings";

    /// <summary>
    /// An instance's settings, as most plugins declare them: plugins:manage, under Admin. The
    /// plugin's admin module provides the screen — <c>SchemaSettings</c> from <c>@dcms/plugin-ui</c>
    /// for the form its config schema describes.
    /// </summary>
    public static AdminScreen Settings { get; } = new(
        SettingsId, "Settings", AdminScreenScope.Instance, IconName: "Settings",
        Titles: new Dictionary<string, string> { ["cs"] = "Nastavení" },
        Description: "The instance's name, description and configuration.")
    {
        Section = AdminScreenSection.Admin,
    };

    /// <summary>
    /// Ids the console's own pages of an app take (<c>/apps/{slug}/data</c>, …, and
    /// <c>/apps/{slug}/content/{type}</c>), so no plugin screen may.
    /// </summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        "data", "integration", "activity", "content",
    };
}

/// <summary>Which group of an app's sub-navigation a screen is in.</summary>
public enum AdminScreenSection
{
    /// <summary>The app's own work — records, an inbox, users. Also listed under the app in the menu.</summary>
    Work,

    /// <summary>Configuring the app, beside its settings and the platform's data, integration and activity pages.</summary>
    Admin,
}

public enum AdminScreenScope
{
    /// <summary>One screen for the whole tenant, across every instance of the plugin.</summary>
    Plugin,

    /// <summary>A screen per instance, shown on that instance's page.</summary>
    Instance,
}

/// <summary>A menu entry for a screen.</summary>
/// <param name="Group">
/// The sidebar section: <c>apps</c> (a plugin's own work — an inbox, a dashboard, an app's
/// records) or <c>manage</c> (beside Sites, Content and Media). Any other value is placed in
/// <c>apps</c>; a plugin cannot add a section of its own.
/// </param>
/// <param name="Order">Position within the section.</param>
public sealed record AdminNavPlacement(string Group = "apps", int Order = 100);
