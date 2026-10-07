using System.Text.Json;
using System.Text.RegularExpressions;
using Dcms.Plugins.UserAuth.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Contracts.Realms;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.UserAuth;
using Dcms.Shared.Security.Realms;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.UserAuth;

public sealed record RealmSettings(bool PasswordEnabled);

public sealed record RoleWrite(string Key, string Name, string? Description, IReadOnlyList<string>? Permissions);

/// <param name="SubjectType"><c>group</c> or <c>user</c>.</param>
public sealed record GrantWrite(string SubjectType, Guid SubjectId);

/// <param name="Access"><c>public</c>, <c>signedIn</c> or <c>groups</c>.</param>
public sealed record GateWrite(string Prefix, string Access, IReadOnlyList<Guid>? Groups);

/// <summary>
/// The plugin's console, <c>/api/admin/plugins/{slug}/…</c> (ADR 0022). Users, groups, providers
/// and the realm itself live in identity: those routes check the member's permission here and
/// pass the call to identity's realm admin API for this tenant, answering with what identity
/// said. Roles, grants and site rules are this plugin's own store; a change to site rules is
/// published to the edge before the call returns.
/// </summary>
internal static partial class UserAuthAdminEndpoints
{
    private const int MaxRules = 100;
    private const int MaxPermissions = 500;

    public static void Map(IPluginEndpointBuilder endpoints)
    {
        // ---- realm ----

        endpoints.MapGet("/realm", async (IPluginContext context, SiteGatePublisher publisher, CancellationToken ct) =>
                await Guarded(async () => await publisher.SyncRealmAsync(context.TenantId, null, ct) is { } realm
                    ? Results.Ok(realm)
                    : Results.NotFound(new { error = "The plugin is not enabled for this tenant." })))
            .RequirePluginPermission("users-read");

        endpoints.MapPut("/realm", async (RealmSettings body, IPluginContext context, SiteGatePublisher publisher, CancellationToken ct) =>
                await Guarded(async () => Results.Ok(await publisher.SyncRealmAsync(context.TenantId, body.PasswordEnabled, ct))))
            .RequirePluginPermission("providers-manage")
            .AuditAs("realm.updated");

        // ---- users, groups and providers: identity's ----

        endpoints.MapGet("/users", (string? search, int? page, int? pageSize, RealmRelay relay, CancellationToken ct) =>
                relay.SendAsync(HttpMethod.Get,
                    $"/users?page={page ?? 1}&pageSize={pageSize ?? 50}" + (string.IsNullOrWhiteSpace(search) ? "" : $"&search={Uri.EscapeDataString(search)}"),
                    null, ct))
            .RequirePluginPermission("users-read");
        endpoints.MapGet("/users/{userId:guid}", (Guid userId, RealmRelay relay, CancellationToken ct) =>
                relay.SendAsync(HttpMethod.Get, $"/users/{userId}", null, ct))
            .RequirePluginPermission("users-read");
        endpoints.MapPost("/users/invite", (RealmInvite body, RealmRelay relay, IPluginContext context, ILogger<RealmRelay> logger, CancellationToken ct) =>
                relay.SendAsync(HttpMethod.Post, "/users/invite", body, ct,
                    then: answer => UserEvents.InvitedAsync(context, JsonSerializer.Deserialize<RealmInviteResult>(answer, JsonSerializerOptions.Web)!.User, logger, ct)))
            .RequirePluginPermission("users-manage")
            .AuditAs("user.invited");
        endpoints.MapPost("/users/{userId:guid}/invite", (Guid userId, RealmRelay relay, CancellationToken ct) =>
                relay.SendAsync(HttpMethod.Post, $"/users/{userId}/invite", null, ct))
            .RequirePluginPermission("users-manage")
            .AuditAs("user.reinvited");
        endpoints.MapPatch("/users/{userId:guid}", (Guid userId, RealmUserPatch body, RealmRelay relay, CancellationToken ct) =>
                relay.SendAsync(HttpMethod.Patch, $"/users/{userId}", body, ct))
            .RequirePluginPermission("users-manage")
            .AuditAs("user.updated");
        endpoints.MapPost("/users/{userId:guid}/sign-out", (Guid userId, RealmRelay relay, CancellationToken ct) =>
                relay.SendAsync(HttpMethod.Post, $"/users/{userId}/sign-out", null, ct))
            .RequirePluginPermission("users-manage")
            .AuditAs("user.signed_out");
        // Their direct grants go with them, once identity has let them go.
        endpoints.MapDelete("/users/{userId:guid}", (Guid userId, RealmRelay relay, UserAuthDbContext db, CancellationToken ct) =>
                relay.SendAsync(HttpMethod.Delete, $"/users/{userId}", null, ct,
                    then: _ => RemoveGrantsAsync(db, GrantSubject.User, userId, ct)))
            .RequirePluginPermission("users-manage")
            .AuditAs("user.deleted");

        endpoints.MapGet("/groups", (RealmRelay relay, CancellationToken ct) =>
                relay.SendAsync(HttpMethod.Get, "/groups", null, ct))
            .RequirePluginPermission("users-read");
        endpoints.MapPost("/groups", (RealmGroupWrite body, RealmRelay relay, CancellationToken ct) =>
                relay.SendAsync(HttpMethod.Post, "/groups", body, ct))
            .RequirePluginPermission("users-manage")
            .AuditAs("group.created");
        endpoints.MapPut("/groups/{groupId:guid}", (Guid groupId, RealmGroupWrite body, RealmRelay relay, CancellationToken ct) =>
                relay.SendAsync(HttpMethod.Put, $"/groups/{groupId}", body, ct))
            .RequirePluginPermission("users-manage")
            .AuditAs("group.updated");
        // Grants to the group go with it. A site rule naming it keeps the id and admits nobody
        // through it: closed, rather than a rule silently changing what it lets in.
        endpoints.MapDelete("/groups/{groupId:guid}", (Guid groupId, RealmRelay relay, UserAuthDbContext db, CancellationToken ct) =>
                relay.SendAsync(HttpMethod.Delete, $"/groups/{groupId}", null, ct,
                    then: _ => RemoveGrantsAsync(db, GrantSubject.Group, groupId, ct)))
            .RequirePluginPermission("users-manage")
            .AuditAs("group.deleted");
        endpoints.MapPut("/groups/{groupId:guid}/members/{userId:guid}", (Guid groupId, Guid userId, RealmRelay relay, CancellationToken ct) =>
                relay.SendAsync(HttpMethod.Put, $"/groups/{groupId}/members/{userId}", null, ct))
            .RequirePluginPermission("users-manage")
            .AuditAs("group.member_added");
        endpoints.MapDelete("/groups/{groupId:guid}/members/{userId:guid}", (Guid groupId, Guid userId, RealmRelay relay, CancellationToken ct) =>
                relay.SendAsync(HttpMethod.Delete, $"/groups/{groupId}/members/{userId}", null, ct))
            .RequirePluginPermission("users-manage")
            .AuditAs("group.member_removed");

        // Client secrets are write-only at identity: never in an answer, so reading providers is a users-read matter.
        endpoints.MapGet("/providers", (RealmRelay relay, CancellationToken ct) =>
                relay.SendAsync(HttpMethod.Get, "/providers", null, ct))
            .RequirePluginPermission("users-read");
        endpoints.MapPut("/providers/{key}", (string key, RealmProviderWrite body, RealmRelay relay, CancellationToken ct) =>
                relay.SendAsync(HttpMethod.Put, $"/providers/{Uri.EscapeDataString(key)}", body, ct))
            .RequirePluginPermission("providers-manage")
            .AuditAs("provider.saved");
        endpoints.MapDelete("/providers/{key}", (string key, RealmRelay relay, CancellationToken ct) =>
                relay.SendAsync(HttpMethod.Delete, $"/providers/{Uri.EscapeDataString(key)}", null, ct))
            .RequirePluginPermission("providers-manage")
            .AuditAs("provider.deleted");

        // ---- roles and grants: this plugin's ----

        endpoints.MapGet("/roles", async (UserAuthDbContext db, CancellationToken ct) =>
            {
                var roles = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
                var grants = await db.Grants.AsNoTracking().ToListAsync(ct);
                return Results.Ok(roles.Select(r => new
                {
                    r.Id, r.Key, r.Name, r.Description, r.Permissions, r.UpdatedAt,
                    grants = grants.Where(g => g.RoleId == r.Id)
                        .Select(g => new { g.Id, subjectType = Camel(g.SubjectType), g.SubjectId, g.CreatedAt }),
                }));
            })
            .RequirePluginPermission("users-read");

        endpoints.MapPost("/roles", async (RoleWrite body, IPluginContext context, UserAuthDbContext db, CancellationToken ct) =>
            {
                if (Invalid(body) is { } error)
                {
                    return error;
                }
                if (await db.Roles.AnyAsync(r => r.Key == body.Key, ct))
                {
                    return Results.Conflict(new { error = $"A role with the key '{body.Key}' already exists." });
                }
                var role = new UserRole
                {
                    Id = Guid.NewGuid(), TenantId = context.TenantId, Key = body.Key, Name = body.Name.Trim(),
                    Description = body.Description?.Trim(), Permissions = Permissions(body),
                };
                db.Roles.Add(role);
                await db.SaveChangesAsync(ct);
                return Results.Created($"roles/{role.Id}", new { role.Id, role.Key, role.Name, role.Description, role.Permissions });
            })
            .RequirePluginPermission("access-manage")
            .AuditAs("role.created");

        endpoints.MapPut("/roles/{roleId:guid}", async (Guid roleId, RoleWrite body, UserAuthDbContext db, CancellationToken ct) =>
            {
                if (Invalid(body) is { } error)
                {
                    return error;
                }
                var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct);
                if (role is null)
                {
                    return Results.NotFound();
                }
                if (role.Key != body.Key && await db.Roles.AnyAsync(r => r.Key == body.Key, ct))
                {
                    return Results.Conflict(new { error = $"A role with the key '{body.Key}' already exists." });
                }
                (role.Key, role.Name, role.Description, role.Permissions, role.UpdatedAt) =
                    (body.Key, body.Name.Trim(), body.Description?.Trim(), Permissions(body), DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { role.Id, role.Key, role.Name, role.Description, role.Permissions });
            })
            .RequirePluginPermission("access-manage")
            .AuditAs("role.updated");

        endpoints.MapDelete("/roles/{roleId:guid}", async (Guid roleId, UserAuthDbContext db, CancellationToken ct) =>
            {
                var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct);
                if (role is null)
                {
                    return Results.NotFound();
                }
                // Its grants cascade.
                db.Roles.Remove(role);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .RequirePluginPermission("access-manage")
            .AuditAs("role.deleted");

        endpoints.MapPost("/roles/{roleId:guid}/grants", async (Guid roleId, GrantWrite body, IPluginContext context, UserAuthDbContext db, CancellationToken ct) =>
            {
                if (!Enum.TryParse<GrantSubject>(body.SubjectType, ignoreCase: true, out var subject) || body.SubjectId == Guid.Empty)
                {
                    return Results.BadRequest(new { error = "A grant names a group or a user by id." });
                }
                if (!await db.Roles.AnyAsync(r => r.Id == roleId, ct))
                {
                    return Results.NotFound();
                }
                var existing = await db.Grants.FirstOrDefaultAsync(g => g.RoleId == roleId && g.SubjectType == subject && g.SubjectId == body.SubjectId, ct);
                if (existing is not null)
                {
                    return Results.Ok(new { existing.Id });
                }
                var grant = new UserGrant
                {
                    Id = Guid.NewGuid(), TenantId = context.TenantId, RoleId = roleId, SubjectType = subject, SubjectId = body.SubjectId,
                    CreatedBy = context.Actor.Id?.ToString() ?? context.Actor.Key,
                };
                db.Grants.Add(grant);
                await db.SaveChangesAsync(ct);
                return Results.Created($"roles/{roleId}/grants/{grant.Id}", new { grant.Id });
            })
            .RequirePluginPermission("access-manage")
            .AuditAs("role.granted");

        endpoints.MapDelete("/roles/{roleId:guid}/grants/{grantId:guid}", async (Guid roleId, Guid grantId, UserAuthDbContext db, CancellationToken ct) =>
            {
                var grant = await db.Grants.FirstOrDefaultAsync(g => g.Id == grantId && g.RoleId == roleId, ct);
                if (grant is null)
                {
                    return Results.NotFound();
                }
                db.Grants.Remove(grant);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .RequirePluginPermission("access-manage")
            .AuditAs("role.revoked");

        // What roles can hold: every enabled plugin's gateable resources (users.resources@1).
        endpoints.MapGet("/resources", async (IPluginContext context, CancellationToken ct) =>
            {
                var catalogs = new List<UserResourceCatalog>();
                foreach (var provider in context.Contracts.GetAll<IUserResources>())
                {
                    catalogs.Add(await provider.ListAsync(ct));
                }
                return Results.Ok(catalogs.OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase));
            })
            .RequirePluginPermission("users-read");

        // ---- site rules ----

        // The tenant's sites with the hostnames rules apply on, and each site's rules in order.
        endpoints.MapGet("/sites", async (SitesDbContext sites, TenancyDbContext tenancy, UserAuthDbContext db, CancellationToken ct) =>
            {
                var list = await sites.Sites.AsNoTracking().OrderBy(s => s.Name).Select(s => new { s.Id, s.Name }).ToListAsync(ct);
                var domains = await tenancy.Domains.AsNoTracking().Where(d => d.SiteId != null)
                    .Select(d => new { d.SiteId, d.Hostname, verified = d.VerifiedAt != null }).ToListAsync(ct);
                var gates = await db.Gates.AsNoTracking().OrderBy(g => g.Position).ToListAsync(ct);
                return Results.Ok(list.Select(s => new
                {
                    s.Id, s.Name,
                    hosts = domains.Where(d => d.SiteId == s.Id).Select(d => new { d.Hostname, d.verified }),
                    rules = gates.Where(g => g.SiteId == s.Id).Select(g => new { prefix = g.PathPrefix, access = Camel(g.Access), g.Groups }),
                }));
            })
            .RequirePluginPermission("users-read");

        // A site's rules, replaced as a whole and in order: the order is the meaning.
        endpoints.MapPut("/sites/{siteId:guid}/rules", async (Guid siteId, List<GateWrite> body, IPluginContext context, UserAuthDbContext db,
                SitesDbContext sites, SiteGatePublisher publisher, CancellationToken ct) =>
            {
                if (!await sites.Sites.AnyAsync(s => s.Id == siteId, ct))
                {
                    return Results.NotFound();
                }
                if (InvalidRules(body) is { } error)
                {
                    return error;
                }
                await using (var tx = await db.Database.BeginTransactionAsync(ct))
                {
                    db.Gates.RemoveRange(await db.Gates.Where(g => g.SiteId == siteId).ToListAsync(ct));
                    // Deleted first: the new rules may reuse a prefix, which is unique per site.
                    await db.SaveChangesAsync(ct);
                    db.Gates.AddRange(body.Select((rule, i) => new SiteGate
                    {
                        Id = Guid.NewGuid(), TenantId = context.TenantId, SiteId = siteId, Position = i,
                        PathPrefix = Prefix(rule.Prefix), Access = Enum.Parse<GateAccess>(rule.Access, ignoreCase: true),
                        Groups = rule.Groups?.Distinct().ToList() ?? [],
                    }));
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                }
                // Saved either way; if the edge or identity cannot be told now, the reconciler
                // tells them within minutes, and the console says so.
                return await Guarded(async () =>
                {
                    await publisher.PublishAsync(context.TenantId, ct);
                    return Results.NoContent();
                }, savedAnyway: true);
            })
            .RequirePluginPermission("access-manage")
            .AuditAs("site_rules.updated");
    }

    private static async Task<IResult> Guarded(Func<Task<IResult>> call, bool savedAnyway = false)
    {
        try
        {
            return await call();
        }
        catch (RealmAdminException e)
        {
            return Results.Json(new { error = savedAnyway ? $"Saved, but not yet live: {e.Message} It will be retried within minutes." : e.Message },
                statusCode: savedAnyway ? StatusCodes.Status202Accepted : e.Status);
        }
        catch (HttpRequestException)
        {
            return Results.Json(new { error = savedAnyway ? "Saved, but not yet live: the sign-in service did not answer. It will be retried within minutes."
                    : "The sign-in service did not answer. Try again in a moment." },
                statusCode: savedAnyway ? StatusCodes.Status202Accepted : StatusCodes.Status502BadGateway);
        }
    }

    private static async Task RemoveGrantsAsync(UserAuthDbContext db, GrantSubject subject, Guid subjectId, CancellationToken ct)
    {
        db.Grants.RemoveRange(await db.Grants.Where(g => g.SubjectType == subject && g.SubjectId == subjectId).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }

    private static IResult? Invalid(RoleWrite body)
    {
        if (!RoleKey().IsMatch(body.Key ?? "") || string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 120
            || body.Description?.Length > 1000)
        {
            return Results.BadRequest(new { error = "A role needs a key (lowercase letters, digits and _, up to 64) and a name of up to 120 characters." });
        }
        var permissions = body.Permissions ?? [];
        if (permissions.Count > MaxPermissions)
        {
            return Results.BadRequest(new { error = $"A role holds at most {MaxPermissions} permissions." });
        }
        return permissions.FirstOrDefault(p => !SitePermission.IsValid(p)) is { } bad
            ? Results.BadRequest(new { error = $"'{bad}' is not a site permission ({{plugin}}:{{instance}}:{{resource}}:{{action}})." })
            : null;
    }

    private static List<string> Permissions(RoleWrite body) =>
        (body.Permissions ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static IResult? InvalidRules(List<GateWrite> rules)
    {
        if (rules.Count > MaxRules)
        {
            return Results.BadRequest(new { error = $"A site has at most {MaxRules} rules." });
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            // What the edge will match against: no encodings or backslashes (it refuses those paths outright).
            if (string.IsNullOrWhiteSpace(rule.Prefix) || !rule.Prefix.StartsWith('/') || rule.Prefix.Length > 512
                || rule.Prefix.Contains('%') || rule.Prefix.Contains('\\') || rule.Prefix.Contains("//") || rule.Prefix.Any(char.IsControl)
                || rule.Prefix.Split('/').Any(s => s is "." or ".."))
            {
                return Results.BadRequest(new { error = $"'{rule.Prefix}' is not a path: it must start with / and contain no %, \\, // or dot segments." });
            }
            if (!seen.Add(Prefix(rule.Prefix)))
            {
                return Results.BadRequest(new { error = $"Two rules for '{rule.Prefix}': only the first would ever apply." });
            }
            if (!Enum.TryParse<GateAccess>(rule.Access, ignoreCase: true, out var access) || !Enum.IsDefined(access))
            {
                return Results.BadRequest(new { error = $"'{rule.Access}' is not an access level (public, signedIn, groups)." });
            }
            if (access == GateAccess.Groups && (rule.Groups is not { Count: > 0 } || rule.Groups.Count > 50))
            {
                return Results.BadRequest(new { error = $"The rule for '{rule.Prefix}' admits groups, so it needs between 1 and 50 of them." });
            }
        }
        return null;
    }

    /// <summary>"/portal/" and "/portal" are one rule; "/" stays "/".</summary>
    private static string Prefix(string prefix) => prefix.Length > 1 ? prefix.TrimEnd('/') : prefix;

    private static string Camel<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex RoleKey();
}

/// <summary>
/// A console call passed to identity's realm admin API for this tenant, answered with what
/// identity said. The realm is made on first use: a 404 for a realm identity does not have yet
/// (the plugin was just enabled) creates it and asks once more.
/// </summary>
public sealed class RealmRelay(RealmAdminClient realms, SiteGatePublisher publisher, IPluginContext context)
{
    /// <param name="then">Run after identity said yes, with what it answered.</param>
    public async Task<IResult> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, Func<string, Task>? then = null)
    {
        var response = await realms.SendAsync(context.TenantId, method, path, body, ct);
        try
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound && await realms.GetRealmAsync(context.TenantId, ct) is null
                && await publisher.SyncRealmAsync(context.TenantId, null, ct) is not null)
            {
                response.Dispose();
                response = await realms.SendAsync(context.TenantId, method, path, body, ct);
            }
            var status = (int)response.StatusCode;
            if (status is 401 or 403 || status >= 500)
            {
                return Results.Json(new { error = "The sign-in service did not answer. Try again in a moment." }, statusCode: StatusCodes.Status502BadGateway);
            }
            var content = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode && then is not null)
            {
                await then(content);
            }
            return content.Length == 0 ? Results.StatusCode(status) : Results.Content(content, "application/json", statusCode: status);
        }
        finally
        {
            response.Dispose();
        }
    }
}
