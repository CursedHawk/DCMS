using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.VisitorAuth.Contracts;

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

/// <summary>Visitor profiles and their tenant-defined attributes.</summary>
[DcmsContract("visitors.profiles", 1,
    Description = "Site visitor profiles and their tenant-defined attributes.",
    Events = [typeof(VisitorRegistered), typeof(VisitorProfileUpdated)])]
public interface IVisitorProfiles
{
    [Operation(OpRisk.Read, Permission = VisitorAuthPlugin.ReadPermission, Expose = OpExposure.Admin | OpExposure.Ai,
        ReturnsExternalText = true, Description = "A visitor's profile: email, display name and attributes shared with plugins.")]
    Task<VisitorProfile?> GetAsync(VisitorRef input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site, ReturnsExternalText = true,
        Description = "A visitor's public profile: display name and public attributes only.")]
    Task<PublicVisitorProfile?> GetPublicAsync(VisitorRef input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "The profile attributes this site defines, with their types and visibility.")]
    Task<AttributeDefinitionList> ListAttributeDefinitionsAsync(CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = VisitorAuthPlugin.ManagePermission, Expose = OpExposure.Admin,
        Description = "Set or clear attributes shared with plugins on a visitor's profile.")]
    Task<VisitorProfile> SetAttributesAsync(SetVisitorAttributes input, CancellationToken ct);
}
