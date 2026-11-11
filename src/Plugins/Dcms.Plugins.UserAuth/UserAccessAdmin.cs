using System.Text.Json;
using System.Text.RegularExpressions;
using Dcms.Plugins.UserAuth.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.UserAuth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.UserAuth;

/// <summary>
/// Roles, grants, site rules and API access: the plugin's own policy store, written the same way
/// from the console's routes and from the assistant's contracts (<c>users.roles@1</c>,
/// <c>users.realm@1</c>). Refusals are contract exceptions — 400 for input to fix, 409 for a
/// clash or an environment that leaves it to deployments — and a missing item is null.
///
/// <para>The routes are also behind the deployment middleware; the check here is for the
/// contract path, which has no route of its own to mark.</para>
/// </summary>
internal sealed partial class UserAccessAdmin(IPluginContext context, IServiceProvider services)
{
    public const string ManagedKind = "user-auth";
    private const int MaxRules = 100;
    private const int MaxPermissions = 500;

    private UserAuthDbContext Db => services.GetRequiredService<UserAuthDbContext>();

    // ---- roles and grants ----

    public async Task<(List<UserRole> Roles, List<UserGrant> Grants)> RolesAsync(CancellationToken ct)
    {
        var roles = await Db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
        var grants = await Db.Grants.AsNoTracking().ToListAsync(ct);
        return (roles, grants);
    }

    public async Task<UserRole> CreateRoleAsync(RoleWrite body, CancellationToken ct)
    {
        Validate(body);
        await EnsureNotManagedAsync($"role:{body.Key}", ct);
        var db = Db;
        if (await db.Roles.AnyAsync(r => r.Key == body.Key, ct))
        {
            throw new ContractConflictException($"A role with the key '{body.Key}' already exists.");
        }
        var role = new UserRole
        {
            Id = Guid.NewGuid(), TenantId = context.TenantId, Key = body.Key, Name = body.Name.Trim(),
            Description = body.Description?.Trim(), Permissions = Permissions(body), BypassRowAccess = body.BypassRowAccess ?? false,
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);
        return role;
    }

    public async Task<UserRole?> UpdateRoleAsync(Guid roleId, RoleWrite body, CancellationToken ct)
    {
        Validate(body);
        var db = Db;
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct);
        if (role is null)
        {
            return null;
        }
        await EnsureNotManagedAsync($"role:{role.Key}", ct);
        await EnsureNotManagedAsync($"role:{body.Key}", ct);
        if (role.Key != body.Key && await db.Roles.AnyAsync(r => r.Key == body.Key, ct))
        {
            throw new ContractConflictException($"A role with the key '{body.Key}' already exists.");
        }
        (role.Key, role.Name, role.Description, role.Permissions, role.BypassRowAccess, role.UpdatedAt) =
            (body.Key, body.Name.Trim(), body.Description?.Trim(), Permissions(body), body.BypassRowAccess ?? role.BypassRowAccess, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return role;
    }

    public async Task<bool> DeleteRoleAsync(Guid roleId, CancellationToken ct)
    {
        var db = Db;
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct);
        if (role is null)
        {
            return false;
        }
        await EnsureNotManagedAsync($"role:{role.Key}", ct);
        // Its grants cascade.
        db.Roles.Remove(role);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <returns>The grant, new (<c>Created</c>) or the one already there; null when the role does not exist.</returns>
    public async Task<(UserGrant Grant, bool Created)?> GrantAsync(Guid roleId, GrantWrite body, CancellationToken ct)
    {
        if (!Enum.TryParse<GrantSubject>(body.SubjectType, ignoreCase: true, out var subject) || !Enum.IsDefined(subject) || body.SubjectId == Guid.Empty)
        {
            throw new ContractValidationException("A grant names a group or a user by id.");
        }
        var db = Db;
        if (!await db.Roles.AnyAsync(r => r.Id == roleId, ct))
        {
            return null;
        }
        var existing = await db.Grants.FirstOrDefaultAsync(g => g.RoleId == roleId && g.SubjectType == subject && g.SubjectId == body.SubjectId, ct);
        if (existing is not null)
        {
            return (existing, false);
        }
        var grant = new UserGrant
        {
            Id = Guid.NewGuid(), TenantId = context.TenantId, RoleId = roleId, SubjectType = subject, SubjectId = body.SubjectId,
            CreatedBy = context.Actor.Id?.ToString() ?? context.Actor.Key,
        };
        db.Grants.Add(grant);
        await db.SaveChangesAsync(ct);
        return (grant, true);
    }

    public async Task<bool> RevokeAsync(Guid roleId, Guid grantId, CancellationToken ct)
    {
        var db = Db;
        var grant = await db.Grants.FirstOrDefaultAsync(g => g.Id == grantId && g.RoleId == roleId, ct);
        if (grant is null)
        {
            return false;
        }
        db.Grants.Remove(grant);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Who was deleted takes their grants along: a user's direct ones, or every grant to a group.</summary>
    public async Task RemoveGrantsAsync(GrantSubject subject, Guid subjectId, CancellationToken ct)
    {
        var db = Db;
        db.Grants.RemoveRange(await db.Grants.Where(g => g.SubjectType == subject && g.SubjectId == subjectId).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>What roles can hold: every enabled plugin's gateable resources (users.resources@1), and permission-gated site APIs.</summary>
    public async Task<List<UserResourceCatalog>> ResourcesAsync(CancellationToken ct)
    {
        var catalogs = new List<UserResourceCatalog>();
        foreach (var provider in context.Contracts.GetAll<IUserResources>())
        {
            catalogs.Add(await provider.ListAsync(ct));
        }
        var byPermission = await Db.ApiRules.Where(r => r.Access == ApiAccess.Permission).Select(r => r.InstanceId).ToListAsync(ct);
        foreach (var instance in (await Plugins.EnabledInstancesAsync(context.TenantId, ct)).Where(i => byPermission.Contains(i.InstanceId)))
        {
            var api = new UserResource("api", "Site API", [new UserResourceAction("read", "Read"), new UserResourceAction("write", "Write")]);
            var index = catalogs.FindIndex(c => c.Plugin == instance.PluginId && c.Instance == instance.Slug);
            if (index >= 0)
            {
                catalogs[index] = catalogs[index] with { Resources = [.. catalogs[index].Resources, api] };
            }
            else
            {
                catalogs.Add(new UserResourceCatalog(instance.PluginId, instance.Slug, instance.Name, [api]));
            }
        }
        return catalogs.OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---- API access ----

    public async Task<List<ApiAccessRule>> ApiAccessAsync(CancellationToken ct)
    {
        var rules = await Db.ApiRules.AsNoTracking().ToDictionaryAsync(r => r.InstanceId, r => r.Access, ct);
        return (await Plugins.EnabledInstancesAsync(context.TenantId, ct))
            .Where(i => i.PluginId != UserAuthPlugin.PluginId)
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(i => new ApiAccessRule(i.InstanceId, i.Slug, i.Name, i.PluginId,
                rules.TryGetValue(i.InstanceId, out var access) ? Camel(access) : "public",
                UserApiAccess.Permission(i.PluginId, i.Slug, write: false),
                UserApiAccess.Permission(i.PluginId, i.Slug, write: true)))
            .ToList();
    }

    /// <returns>False when no enabled instance (other than this plugin) has that id.</returns>
    public async Task<bool> SetApiAccessAsync(Guid instanceId, ApiAccessWrite body, CancellationToken ct)
    {
        var instance = (await Plugins.EnabledInstancesAsync(context.TenantId, ct)).FirstOrDefault(i => i.InstanceId == instanceId);
        if (instance is null || instance.PluginId == UserAuthPlugin.PluginId)
        {
            return false;
        }
        var isPublic = string.Equals(body.Access, "public", StringComparison.OrdinalIgnoreCase);
        var access = ApiAccess.SignedIn;
        if (!isPublic && (!Enum.TryParse(body.Access, ignoreCase: true, out access) || !Enum.IsDefined(access)))
        {
            throw new ContractValidationException($"'{body.Access}' is not an API access level (public, signedIn, permission).");
        }
        await EnsureNotManagedAsync($"api:{instance.Slug}", ct);
        var db = Db;
        var rule = await db.ApiRules.FirstOrDefaultAsync(r => r.InstanceId == instanceId, ct);
        if (isPublic)
        {
            if (rule is not null)
            {
                db.ApiRules.Remove(rule);
            }
        }
        else if (rule is null)
        {
            db.ApiRules.Add(new ApiRule { Id = Guid.NewGuid(), TenantId = context.TenantId, InstanceId = instanceId, Access = access });
        }
        else
        {
            rule.Access = access;
            rule.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ---- site rules ----

    /// <summary>The tenant's sites with the hostnames rules apply on, and each site's rules in order.</summary>
    public async Task<List<SiteRules>> SitesAsync(CancellationToken ct)
    {
        var list = await services.GetRequiredService<SitesDbContext>().Sites.AsNoTracking().OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.RenderMode }).ToListAsync(ct);
        var domains = await services.GetRequiredService<TenancyDbContext>().Domains.AsNoTracking().Where(d => d.SiteId != null)
            .Select(d => new { d.SiteId, d.Hostname }).ToListAsync(ct);
        var gates = await Db.Gates.AsNoTracking().OrderBy(g => g.Position).ToListAsync(ct);
        return list.Select(s => new SiteRules(
                s.Id, s.Name,
                // A single-page app: its route changes never reach the edge, so its rules guard direct loads only.
                s.RenderMode.IsViteApp(),
                domains.Where(d => d.SiteId == s.Id).Select(d => d.Hostname).ToList(),
                gates.Where(g => g.SiteId == s.Id).Select(g => new SiteRule(g.PathPrefix, Camel(g.Access), g.Groups)).ToList()))
            .ToList();
    }

    /// <summary>
    /// A site's rules, replaced as a whole and in order (the order is the meaning), then published
    /// to the edge. False when the site does not exist. A failed publish leaves them saved: the
    /// reconciler tells the edge within minutes, and the exception says so to the caller.
    /// </summary>
    public async Task<bool> SetSiteRulesAsync(Guid siteId, IReadOnlyList<GateWrite> rules, CancellationToken ct)
    {
        if (!await services.GetRequiredService<SitesDbContext>().Sites.AnyAsync(s => s.Id == siteId, ct))
        {
            return false;
        }
        ValidateRules(rules);
        var db = Db;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            db.Gates.RemoveRange(await db.Gates.Where(g => g.SiteId == siteId).ToListAsync(ct));
            // Deleted first: the new rules may reuse a prefix, which is unique per site.
            await db.SaveChangesAsync(ct);
            db.Gates.AddRange(rules.Select((rule, i) => new SiteGate
            {
                Id = Guid.NewGuid(), TenantId = context.TenantId, SiteId = siteId, Position = i,
                PathPrefix = Prefix(rule.Prefix), Access = Enum.Parse<GateAccess>(rule.Access, ignoreCase: true),
                Groups = rule.Groups?.Distinct().ToList() ?? [],
            }));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        await services.GetRequiredService<SiteGatePublisher>().PublishAsync(context.TenantId, ct);
        return true;
    }

    // ---- shared ----

    private PluginContextFactory Plugins => services.GetRequiredService<PluginContextFactory>();

    /// <summary>The same refusal the deployment middleware gives the routes, for the contract path.</summary>
    private async Task EnsureNotManagedAsync(string key, CancellationToken ct)
    {
        if (await EnvironmentPolicies.IsManagedAsync(services.GetRequiredService<TenancyDbContext>(), context.TenantId, context.EnvironmentId,
                ManagedKind, key, ct))
        {
            throw new ContractConflictException(
                "This is managed by deployments in this environment: change it in the environment that deploys here, and deploy it.");
        }
    }

    private static void Validate(RoleWrite body)
    {
        if (!RoleKey().IsMatch(body.Key ?? "") || string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 120
            || body.Description?.Length > 1000)
        {
            throw new ContractValidationException("A role needs a key (lowercase letters, digits and _, up to 64) and a name of up to 120 characters.");
        }
        var permissions = body.Permissions ?? [];
        if (permissions.Count > MaxPermissions)
        {
            throw new ContractValidationException($"A role holds at most {MaxPermissions} permissions.");
        }
        if (permissions.FirstOrDefault(p => !SitePermission.IsValid(p)) is { } bad)
        {
            throw new ContractValidationException($"'{bad}' is not a site permission ({{plugin}}:{{instance}}:{{resource}}:{{action}}).");
        }
    }

    private static List<string> Permissions(RoleWrite body) =>
        (body.Permissions ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static void ValidateRules(IReadOnlyList<GateWrite> rules)
    {
        if (rules.Count > MaxRules)
        {
            throw new ContractValidationException($"A site has at most {MaxRules} rules.");
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            // What the edge will match against: no encodings or backslashes (it refuses those paths outright).
            if (string.IsNullOrWhiteSpace(rule.Prefix) || !rule.Prefix.StartsWith('/') || rule.Prefix.Length > 512
                || rule.Prefix.Contains('%') || rule.Prefix.Contains('\\') || rule.Prefix.Contains("//") || rule.Prefix.Any(char.IsControl)
                || rule.Prefix.Split('/').Any(s => s is "." or ".."))
            {
                throw new ContractValidationException($"'{rule.Prefix}' is not a path: it must start with / and contain no %, \\, // or dot segments.");
            }
            if (!seen.Add(Prefix(rule.Prefix)))
            {
                throw new ContractValidationException($"Two rules for '{rule.Prefix}': only the first would ever apply.");
            }
            if (!Enum.TryParse<GateAccess>(rule.Access, ignoreCase: true, out var access) || !Enum.IsDefined(access))
            {
                throw new ContractValidationException($"'{rule.Access}' is not an access level (public, signedIn, groups).");
            }
            if (access == GateAccess.Groups && (rule.Groups is not { Count: > 0 } || rule.Groups.Count > 50))
            {
                throw new ContractValidationException($"The rule for '{rule.Prefix}' admits groups, so it needs between 1 and 50 of them.");
            }
        }
    }

    /// <summary>"/portal/" and "/portal" are one rule; "/" stays "/".</summary>
    private static string Prefix(string prefix) => prefix.Length > 1 ? prefix.TrimEnd('/') : prefix;

    public static string Camel<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex RoleKey();
}
