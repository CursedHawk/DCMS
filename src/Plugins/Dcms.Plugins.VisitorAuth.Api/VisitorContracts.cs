using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.VisitorAuth.Api;

/// <summary>Permission keys the visitor contracts' external operations require.</summary>
public static class VisitorPermissions
{
    public const string Read = "plugin:visitor-auth:read";
    public const string Manage = "plugin:visitor-auth:manage";
}

/// <summary>
/// A visitor as other plugins see them. <see cref="Attributes"/> holds only values whose
/// definition is visible to plugins (or public) — never a private attribute.
/// </summary>
public sealed record VisitorProfile(
    Guid Id,
    string Email,
    string? DisplayName,
    IReadOnlyDictionary<string, JsonElement> Attributes,
    DateTimeOffset CreatedAt);

/// <summary>What anyone may see: the display name and public attributes. No email.</summary>
public sealed record PublicVisitorProfile(Guid Id, string? DisplayName, IReadOnlyDictionary<string, JsonElement> Attributes);

public sealed record CurrentVisitor(VisitorProfile? Visitor);

public sealed record VisitorRef(Guid VisitorId);

/// <param name="Attributes">Values by key; a JSON null removes one. Only non-private attributes may be set here.</param>
public sealed record SetVisitorAttributes(Guid VisitorId, IReadOnlyDictionary<string, JsonElement> Attributes);

public sealed record AttributeDefinitionList(IReadOnlyList<AttributeDefinition> Definitions);

[ContractEvent("visitor.registered")]
public sealed record VisitorRegistered(Guid VisitorId) : IPluginEvent;

[ContractEvent("visitor.profile.updated")]
public sealed record VisitorProfileUpdated(Guid VisitorId, IReadOnlyList<string> ChangedKeys) : IPluginEvent;

/// <summary>Who the signed-in site visitor is — for plugins serving the public site.</summary>
[DcmsContract("visitors.identity", 1, Description = "The signed-in site visitor of the current request, if any.")]
public interface IVisitorIdentity
{
    /// <summary>Null visitor for an anonymous request (or outside a request).</summary>
    [Operation(OpRisk.Read, Expose = OpExposure.Site, ReturnsExternalText = true,
        Description = "The signed-in visitor, with the attributes visible to plugins; null when nobody is signed in.")]
    Task<CurrentVisitor> GetCurrentAsync(CancellationToken ct);
}

public static class VisitorIdentityExtensions
{
    /// <summary>
    /// The signed-in visitor, from whichever provider recognises the request: VisitorAuth for a
    /// visitor token, User Authentication for an enterprise user (ADR 0022). Null when none does,
    /// or when no provider is enabled.
    /// </summary>
    public static async Task<VisitorProfile?> CurrentVisitorAsync(this IPluginContracts contracts, CancellationToken ct)
    {
        foreach (var identity in contracts.GetAll<IVisitorIdentity>())
        {
            if ((await identity.GetCurrentAsync(ct)).Visitor is { } visitor)
            {
                return visitor;
            }
        }
        return null;
    }
}

/// <summary>Visitor profiles and their tenant-defined attributes.</summary>
[DcmsContract("visitors.profiles", 1,
    Description = "Site visitor profiles and their tenant-defined attributes.",
    Events = [typeof(VisitorRegistered), typeof(VisitorProfileUpdated)])]
public interface IVisitorProfiles
{
    [Operation(OpRisk.Read, Permission = VisitorPermissions.Read, Expose = OpExposure.Admin | OpExposure.Ai,
        ReturnsExternalText = true, Description = "A visitor's profile: email, display name and attributes shared with plugins.")]
    Task<VisitorProfile?> GetAsync(VisitorRef input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site, ReturnsExternalText = true,
        Description = "A visitor's public profile: display name and public attributes only.")]
    Task<PublicVisitorProfile?> GetPublicAsync(VisitorRef input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "The profile attributes this site defines, with their types and visibility.")]
    Task<AttributeDefinitionList> ListAttributeDefinitionsAsync(CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = VisitorPermissions.Manage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Set or clear attributes shared with plugins on a visitor's profile.")]
    Task<VisitorProfile> SetAttributesAsync(SetVisitorAttributes input, CancellationToken ct);
}

/// <summary>
/// Who may see an attribute's value, beyond the visitor it belongs to and the tenant's admins.
/// Ordered: each level includes the audiences of the ones before it.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(CamelCaseEnumConverter<AttributeVisibility>))]
public enum AttributeVisibility
{
    /// <summary>The visitor and admins only. Never handed to another plugin.</summary>
    Private,

    /// <summary>Also other plugins, server-side, through <c>visitors.profiles@1</c>.</summary>
    Plugins,

    /// <summary>Also anyone: shown on the visitor's public profile (an author card, a roster entry).</summary>
    Public,
}

[System.Text.Json.Serialization.JsonConverter(typeof(CamelCaseEnumConverter<AttributeType>))]
public enum AttributeType
{
    Text,
    Number,
    Boolean,
    Date,
    Select,
}

/// <summary>One profile attribute, defined by the tenant in the VisitorAuth instance config.</summary>
public sealed record AttributeDefinition(
    string Key,
    string Label,
    AttributeType Type = AttributeType.Text,
    AttributeVisibility Visibility = AttributeVisibility.Private,
    bool VisitorEditable = true,
    IReadOnlyList<string>? Options = null);

/// <summary>Enums on the wire as the lowercase names the config schema uses ("plugins", "select").</summary>
public sealed class CamelCaseEnumConverter<T>() : System.Text.Json.Serialization.JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase)
    where T : struct, Enum;

/// <param name="Search">Part of an email or display name; all visitors, newest first, when empty.</param>
public sealed record VisitorSearch(string? Search = null, int Page = 1, int PageSize = 25);

public sealed record VisitorAccountInfo(Guid Id, string Email, string? DisplayName, bool EmailVerified, DateTimeOffset CreatedAt);

public sealed record VisitorAccountPage(IReadOnlyList<VisitorAccountInfo> Items, long Total);

/// <summary>
/// The site's visitor accounts as the plugin's admin page manages them, for the assistant. A
/// contract of its own: functions that read profiles vouch for <see cref="IVisitorProfiles"/>,
/// and reading must not start to need — or grant — deleting accounts.
/// </summary>
[DcmsContract("visitors.accounts", 1, Description = "Find site visitor accounts, sign them out, mark them verified or delete them.")]
public interface IVisitorAccounts
{
    [Operation(OpRisk.Read, Permission = VisitorPermissions.Read, Expose = OpExposure.Admin | OpExposure.Ai, ReturnsExternalText = true,
        Description = "Visitor accounts matching an email or name, newest first.")]
    Task<VisitorAccountPage> FindAsync(VisitorSearch input, CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = VisitorPermissions.Manage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "End a visitor's sessions everywhere: they sign in again once their current access token expires (minutes).")]
    Task SignOutEverywhereAsync(VisitorRef input, CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = VisitorPermissions.Manage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Mark a visitor's email address verified.")]
    Task MarkEmailVerifiedAsync(VisitorRef input, CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = VisitorPermissions.Manage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Delete a visitor account and its sessions for good.")]
    Task DeleteAsync(VisitorRef input, CancellationToken ct);
}
