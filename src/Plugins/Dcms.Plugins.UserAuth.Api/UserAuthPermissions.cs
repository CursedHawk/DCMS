using System.Text.RegularExpressions;

namespace Dcms.Plugins.UserAuth.Api;

/// <summary>
/// The console permissions of the User Authentication plugin: who may manage a tenant's
/// enterprise users and what they may reach. Not to be confused with the permissions those
/// users hold on the tenant's sites, which are data (<see cref="SitePermission"/>).
/// </summary>
public static class UserAuthPermissions
{
    public const string PluginId = "user-auth";

    public const string UsersRead = $"plugin:{PluginId}:users-read";
    public const string UsersManage = $"plugin:{PluginId}:users-manage";

    /// <summary>Roles, grants and site access rules: who may reach what.</summary>
    public const string AccessManage = $"plugin:{PluginId}:access-manage";

    /// <summary>Sign-in providers, including their client secrets (write-only).</summary>
    public const string ProvidersManage = $"plugin:{PluginId}:providers-manage";
}

/// <summary>
/// A permission an enterprise user can hold on a tenant's sites:
/// <c>{plugin}:{instance}:{resource}:{action}</c>, e.g. <c>dynamic-apps:crm:table:deals:read</c>.
/// The resource may itself have parts (<c>table:deals</c>); the plugin and instance never do.
/// </summary>
public static partial class SitePermission
{
    public const int MaxLength = 200;

    public static bool IsValid(string? key) =>
        key is { Length: > 0 and <= MaxLength } && Shape().IsMatch(key);

    public static string Of(string plugin, string instance, string resource, string action) =>
        $"{plugin}:{instance}:{resource}:{action}";

    // plugin and instance: lowercase slugs; resource: one or more slug parts; action: a slug.
    [GeneratedRegex("^[a-z][a-z0-9-]*:[a-z0-9][a-z0-9-]*(:[a-z0-9][a-z0-9_.-]*){2,4}$")]
    private static partial Regex Shape();
}
