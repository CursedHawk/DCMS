using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

using Dcms.Shared.Data;
namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// What makes an environment link valid (ADR 0026, 0028), for every way one is made: the links
/// endpoint, and a deployment carrying one (the <c>links</c> kind). A rule enforced in only one of
/// them would be a way round it.
/// </summary>
public static class EnvironmentLinkRules
{
    /// <summary>A link as it may be made.</summary>
    public sealed record Checked(string Kind, string? TargetKey, EnvironmentLinkAccess Access, List<string> Grants);

    /// <summary>Why it may not be: <paramref name="Conflict"/> when it collides with a link that exists, else bad input.</summary>
    public sealed record Refused(string Error, bool Conflict = false);

    private const string DynamicAppsPluginId = "dynamic-apps";

    private static readonly string[] TableVerbs = ["read", "create", "update", "delete"];

    /// <param name="existing">The link being replaced, when a deployment updates one: not a collision with itself.</param>
    public static async Task<(Checked? Link, Refused? Refused)> CheckAsync(
        TenancyDbContext db, CmsDbContext cms, PluginRegistry registry, TenantEnvironment consumer, TenantEnvironment provider,
        string kind, string? targetKey, string? access, IReadOnlyCollection<string>? grants, Guid? existing, CancellationToken ct)
    {
        var tenantId = consumer.TenantId;
        if (provider.Id == consumer.Id || provider.TenantId != tenantId)
        {
            return (null, new Refused("A link names another environment of this workspace."));
        }
        targetKey = targetKey?.Trim();
        switch (kind)
        {
            case EnvironmentLinkKinds.Users:
            {
                // Sign-in through the target's realm. One realm per environment, and never a
                // chain: the target signs its own people in, and nobody signs in through this one.
                // rls: platform -- the workspace's users links, every environment's, named by tenant.
                using var platform = RlsScope.Platform();
                if (await db.EnvironmentLinks.IgnoreQueryFilters().AnyAsync(l => l.TenantId == tenantId && l.Kind == EnvironmentLinkKinds.Users
                        && l.Id != existing
                        && (l.EnvironmentId == consumer.Id || l.EnvironmentId == provider.Id || l.TargetEnvironmentId == consumer.Id), ct))
                {
                    return (null, new Refused("An environment takes its users from one other environment, which keeps its own.", Conflict: true));
                }
                return (new Checked(kind, null, EnvironmentLinkAccess.Read, []), null);
            }
            case EnvironmentLinkKinds.Contracts or EnvironmentLinkKinds.Tables:
            {
                // A plugin here calls an instance there (ADR 0028): what it may call is listed, not implied.
                if (string.IsNullOrWhiteSpace(targetKey) || grants is not { Count: > 0 and <= 100 } || grants.Any(g => !Grant(kind, g)))
                {
                    return (null, new Refused(kind == EnvironmentLinkKinds.Tables
                        ? "A tables link names a Dynamic Apps instance there and grants table:read, table:create, table:update or table:delete."
                        : "A contracts link names an instance there and grants contract/Operation."));
                }
                using (provider.Scope())
                {
                    var instance = await cms.PluginInstances.IgnoreTenantFilter().AsNoTracking()
                        .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Slug == targetKey, ct);
                    if (instance is null || (kind == EnvironmentLinkKinds.Tables && instance.PluginId != DynamicAppsPluginId))
                    {
                        return (null, new Refused("That environment has no such instance to link to."));
                    }
                    // Operations it really provides: a grant naming nothing would wait silently for a call that never works.
                    if (kind == EnvironmentLinkKinds.Contracts
                        && grants.Select(g => g.Trim()).Except(Operations(registry, instance.PluginId).Select(o => o.Grant)).FirstOrDefault() is { } unknown)
                    {
                        return (null, new Refused($"'{targetKey}' there provides no operation {unknown}."));
                    }
                }
                using (consumer.Scope())
                {
                    if (await db.EnvironmentLinks.IgnoreTenantFilter().AnyAsync(l => l.TenantId == tenantId && l.Id != existing && l.Kind == kind
                            && l.TargetEnvironmentId == provider.Id && l.TargetKey == targetKey, ct))
                    {
                        return (null, new Refused("This instance is already linked.", Conflict: true));
                    }
                }
                return (new Checked(kind, targetKey, EnvironmentLinkAccess.ReadWrite,
                    grants.Select(g => g.Trim()).Distinct(StringComparer.Ordinal).ToList()), null);
            }
            case EnvironmentLinkKinds.App:
            {
                if (string.IsNullOrWhiteSpace(targetKey) || !Enum.TryParse<EnvironmentLinkAccess>(access, ignoreCase: true, out var appAccess))
                {
                    return (null, new Refused("An app link names the app's address and read or readWrite access."));
                }
                // Only an app the target has: anything else under /api is the platform's own routes.
                using (provider.Scope())
                {
                    if (!await cms.PluginInstances.IgnoreTenantFilter().AnyAsync(p => p.TenantId == tenantId && p.Slug == targetKey, ct))
                    {
                        return (null, new Refused("That environment has no app at this address."));
                    }
                }
                using (consumer.Scope())
                {
                    if (await db.EnvironmentLinks.IgnoreTenantFilter().AnyAsync(l => l.TenantId == tenantId && l.Id != existing
                            && l.Kind == kind && l.TargetKey == targetKey, ct))
                    {
                        return (null, new Refused("This app is already linked.", Conflict: true));
                    }
                    // One address, one app: a link would hide this environment's own app from its sites.
                    if (await cms.PluginInstances.IgnoreTenantFilter().AnyAsync(p => p.TenantId == tenantId && p.Slug == targetKey, ct))
                    {
                        return (null, new Refused("This environment has its own app at this address.", Conflict: true));
                    }
                }
                return (new Checked(kind, targetKey, appAccess, []), null);
            }
            default:
                return (null, new Refused("A link is an app, users, contracts or tables link."));
        }
    }

    /// <summary>
    /// Whether an app link of the environment the caller is in already serves this address from
    /// elsewhere: an app of its own there would never be reached.
    /// </summary>
    public static Task<bool> AppLinkedAsync(TenancyDbContext db, Guid tenantId, string slug, CancellationToken ct) =>
        db.EnvironmentLinks.IgnoreTenantFilter()
            .AnyAsync(l => l.TenantId == tenantId && l.Kind == EnvironmentLinkKinds.App && l.TargetKey == slug, ct);

    /// <summary>What a contracts link to an instance of <paramref name="pluginId"/> may grant: the operations its contracts offer to other environments (<see cref="OpExposure.Link"/>).</summary>
    public static IEnumerable<(string Grant, OpRisk Risk, string? Description)> Operations(PluginRegistry registry, string pluginId) =>
        registry.Contracts
            .Where(c => !c.Descriptor.IsPlatform && c.ProvidedBy(pluginId) is not null)
            .OrderBy(c => c.Descriptor.Name, StringComparer.Ordinal)
            .SelectMany(c => c.Descriptor.Operations.Where(o => o.Expose.HasFlag(OpExposure.Link))
                .Select(o => ($"{c.Descriptor.Name}/{o.Name}", o.Risk, o.Description)));

    /// <summary>A grant of the link's kind: <c>contract/Operation</c>, or <c>table:verb</c>.</summary>
    public static bool Grant(string kind, string grant)
    {
        var g = grant.Trim();
        if (g.Length is 0 or > 200) return false;
        if (kind == EnvironmentLinkKinds.Tables)
        {
            var colon = g.LastIndexOf(':');
            return colon > 0 && TableVerbs.Contains(g[(colon + 1)..]);
        }
        var slash = g.IndexOf('/');
        return slash > 0 && slash < g.Length - 1 && g.IndexOf('/', slash + 1) < 0;
    }
}
