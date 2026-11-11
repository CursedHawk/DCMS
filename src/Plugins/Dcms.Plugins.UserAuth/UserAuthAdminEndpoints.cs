using System.Text.Json;
using Dcms.Plugins.UserAuth.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime.Contracts;
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

/// <param name="BypassRowAccess">Null on an update keeps what the role has.</param>
public sealed record RoleWrite(string Key, string Name, string? Description, IReadOnlyList<string>? Permissions, bool? BypassRowAccess = null);

/// <param name="SubjectType"><c>group</c> or <c>user</c>.</param>
public sealed record GrantWrite(string SubjectType, Guid SubjectId);

/// <param name="Access"><c>public</c>, <c>signedIn</c> or <c>permission</c>.</param>
public sealed record ApiAccessWrite(string Access);

/// <param name="Access"><c>public</c>, <c>signedIn</c> or <c>groups</c>.</param>
public sealed record GateWrite(string Prefix, string Access, IReadOnlyList<Guid>? Groups);

/// <summary>
/// The plugin's console, <c>/api/admin/plugins/{slug}/…</c> (ADR 0022). Users, groups, providers
/// and the realm itself live in identity: those routes check the member's permission here and
/// pass the call to identity's realm admin API for this tenant, answering with what identity
/// said. Roles, grants and site rules are this plugin's own store; a change to site rules is
/// published to the edge before the call returns.
/// </summary>
internal static class UserAuthAdminEndpoints
{
    public static void Map(IPluginEndpointBuilder endpoints)
    {
        // ---- realm ----

        endpoints.MapGet("/realm", async (IPluginContext context, SiteGatePublisher publisher, RealmRelay relay, CancellationToken ct) =>
                await Guarded(async () => await publisher.SyncRealmAsync(await relay.RealmTenantAsync(ct), null, ct) is { } realm
                    ? Results.Ok(realm)
                    : Results.NotFound(new { error = "The plugin is not enabled for this tenant." })))
            .RequirePluginPermission("users-read");

        endpoints.MapPut("/realm", async (RealmSettings body, IPluginContext context, SiteGatePublisher publisher, RealmRelay relay, CancellationToken ct) =>
                await relay.RealmTenantAsync(ct) != context.EnvironmentId
                    ? RealmRelay.Linked
                    : await Guarded(async () => Results.Ok(await publisher.SyncRealmAsync(context.EnvironmentId, body.PasswordEnabled, ct))))
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
        // "View as" in a site builder's preview (#269): a short-lived token for this user, asked for
        // by the edge with the member's own session and kept in the edge's preview session — never
        // handed to the browser. Managing users is the bar: it is acting as one of them.
        endpoints.MapPost("/users/{userId:guid}/preview-token", (Guid userId, HttpContext http, RealmRelay relay, CancellationToken ct) =>
                Guid.TryParse(http.User.FindFirst("sub")?.Value, out var me)
                    ? relay.SendAsync(HttpMethod.Post, $"/users/{userId}/preview-token", new RealmPreviewTokenRequest(me), ct)
                    : Task.FromResult(Results.Unauthorized()))
            .RequirePluginPermission("users-manage")
            .AuditAs("user.previewed");
        // Their direct grants go with them, once identity has let them go.
        endpoints.MapDelete("/users/{userId:guid}", (Guid userId, RealmRelay relay, IPluginContext context, IServiceProvider services, CancellationToken ct) =>
                relay.SendAsync(HttpMethod.Delete, $"/users/{userId}", null, ct,
                    then: _ => new UserAccessAdmin(context, services).RemoveGrantsAsync(GrantSubject.User, userId, ct)))
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
        endpoints.MapDelete("/groups/{groupId:guid}", (Guid groupId, RealmRelay relay, IPluginContext context, IServiceProvider services, CancellationToken ct) =>
                relay.SendAsync(HttpMethod.Delete, $"/groups/{groupId}", null, ct,
                    then: _ => new UserAccessAdmin(context, services).RemoveGrantsAsync(GrantSubject.Group, groupId, ct)))
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

        // ---- roles and grants: this plugin's (UserAccessAdmin, shared with users.roles@1) ----

        endpoints.MapGet("/roles", async (IPluginContext context, IServiceProvider services, CancellationToken ct) =>
            {
                var (roles, grants) = await new UserAccessAdmin(context, services).RolesAsync(ct);
                return Results.Ok(roles.Select(r => new
                {
                    r.Id, r.Key, r.Name, r.Description, r.Permissions, r.BypassRowAccess, r.UpdatedAt,
                    grants = grants.Where(g => g.RoleId == r.Id)
                        .Select(g => new { g.Id, subjectType = UserAccessAdmin.Camel(g.SubjectType), g.SubjectId, g.CreatedAt }),
                }));
            })
            .RequirePluginPermission("users-read");

        endpoints.MapPost("/roles", (RoleWrite body, IPluginContext context, IServiceProvider services, CancellationToken ct) =>
                Run(async () =>
                {
                    var role = await new UserAccessAdmin(context, services).CreateRoleAsync(body, ct);
                    return Results.Created($"roles/{role.Id}", new { role.Id, role.Key, role.Name, role.Description, role.Permissions, role.BypassRowAccess });
                }))
            .RequirePluginPermission("access-manage")
            .AuditAs("role.created")
            .ManagedByDeployments("user-auth");

        endpoints.MapPut("/roles/{roleId:guid}", (Guid roleId, RoleWrite body, IPluginContext context, IServiceProvider services, CancellationToken ct) =>
                Run(async () => await new UserAccessAdmin(context, services).UpdateRoleAsync(roleId, body, ct) is { } role
                    ? Results.Ok(new { role.Id, role.Key, role.Name, role.Description, role.Permissions, role.BypassRowAccess })
                    : Results.NotFound()))
            .RequirePluginPermission("access-manage")
            .AuditAs("role.updated")
            .ManagedByDeployments("user-auth");

        endpoints.MapDelete("/roles/{roleId:guid}", (Guid roleId, IPluginContext context, IServiceProvider services, CancellationToken ct) =>
                Run(async () => await new UserAccessAdmin(context, services).DeleteRoleAsync(roleId, ct) ? Results.NoContent() : Results.NotFound()))
            .RequirePluginPermission("access-manage")
            .AuditAs("role.deleted")
            .ManagedByDeployments("user-auth");

        endpoints.MapPost("/roles/{roleId:guid}/grants", (Guid roleId, GrantWrite body, IPluginContext context, IServiceProvider services, CancellationToken ct) =>
                Run(async () => await new UserAccessAdmin(context, services).GrantAsync(roleId, body, ct) switch
                {
                    null => Results.NotFound(),
                    ({ } grant, true) => Results.Created($"roles/{roleId}/grants/{grant.Id}", new { grant.Id }),
                    ({ } grant, false) => Results.Ok(new { grant.Id }),
                }))
            .RequirePluginPermission("access-manage")
            .AuditAs("role.granted");

        endpoints.MapDelete("/roles/{roleId:guid}/grants/{grantId:guid}", (Guid roleId, Guid grantId, IPluginContext context, IServiceProvider services,
                CancellationToken ct) =>
                Run(async () => await new UserAccessAdmin(context, services).RevokeAsync(roleId, grantId, ct) ? Results.NoContent() : Results.NotFound()))
            .RequirePluginPermission("access-manage")
            .AuditAs("role.revoked");

        // What roles can hold: every enabled plugin's gateable resources (users.resources@1).
        endpoints.MapGet("/resources", async (IPluginContext context, IServiceProvider services, CancellationToken ct) =>
                Results.Ok(await new UserAccessAdmin(context, services).ResourcesAsync(ct)))
            .RequirePluginPermission("users-read");

        // ---- API access: who may call each instance's /api/{slug} ----

        endpoints.MapGet("/api-access", async (IPluginContext context, IServiceProvider services, CancellationToken ct) =>
                Results.Ok(await new UserAccessAdmin(context, services).ApiAccessAsync(ct)))
            .RequirePluginPermission("users-read");

        endpoints.MapPut("/api-access/{instanceId:guid}", (Guid instanceId, ApiAccessWrite body, IPluginContext context, IServiceProvider services,
                CancellationToken ct) =>
                Run(async () => await new UserAccessAdmin(context, services).SetApiAccessAsync(instanceId, body, ct) ? Results.NoContent() : Results.NotFound()))
            .RequirePluginPermission("access-manage")
            .AuditAs("api_access.updated")
            .ManagedByDeployments("user-auth");

        // ---- site rules ----

        // The tenant's sites with the hostnames rules apply on, and each site's rules in order.
        endpoints.MapGet("/sites", async (SitesDbContext sites, TenancyDbContext tenancy, UserAuthDbContext db, CancellationToken ct) =>
            {
                var list = await sites.Sites.AsNoTracking().OrderBy(s => s.Name).Select(s => new { s.Id, s.Name, s.RenderMode }).ToListAsync(ct);
                var domains = await tenancy.Domains.AsNoTracking().Where(d => d.SiteId != null)
                    .Select(d => new { d.SiteId, d.Hostname, verified = d.VerifiedAt != null }).ToListAsync(ct);
                var gates = await db.Gates.AsNoTracking().OrderBy(g => g.Position).ToListAsync(ct);
                return Results.Ok(list.Select(s => new
                {
                    s.Id, s.Name,
                    // A single-page app: its route changes never reach the edge, so its rules guard direct loads only.
                    spa = s.RenderMode.IsViteApp(),
                    hosts = domains.Where(d => d.SiteId == s.Id).Select(d => new { d.Hostname, d.verified }),
                    rules = gates.Where(g => g.SiteId == s.Id).Select(g => new { prefix = g.PathPrefix, access = UserAccessAdmin.Camel(g.Access), g.Groups }),
                }));
            })
            .RequirePluginPermission("users-read");

        // A site's rules, replaced as a whole and in order: the order is the meaning. Saved either
        // way; if the edge or identity cannot be told now, the reconciler tells them within
        // minutes, and the console says so.
        endpoints.MapPut("/sites/{siteId:guid}/rules", (Guid siteId, List<GateWrite> body, IPluginContext context, IServiceProvider services,
                CancellationToken ct) =>
                Run(() => Guarded(async () =>
                    await new UserAccessAdmin(context, services).SetSiteRulesAsync(siteId, body, ct) ? Results.NoContent() : Results.NotFound(),
                    savedAnyway: true)))
            .RequirePluginPermission("access-manage")
            .AuditAs("site_rules.updated");
    }

    /// <summary>The service's refusals as the console has always had them: 400 for input to fix, 409 for a clash.</summary>
    private static async Task<IResult> Run(Func<Task<IResult>> call)
    {
        try
        {
            return await call();
        }
        catch (ContractValidationException e)
        {
            return Results.BadRequest(new { error = e.Message });
        }
        catch (ContractConflictException e)
        {
            return Results.Conflict(new { error = e.Message });
        }
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
}

/// <summary>
/// A console call passed to identity's realm admin API for this tenant, answered with what
/// identity said. The realm is made on first use: a 404 for a realm identity does not have yet
/// (the plugin was just enabled) creates it and asks once more.
///
/// <para>An environment whose sites take their users from another (ADR 0026) reads that realm,
/// and changes nothing in it: its people are managed where they live.</para>
/// </summary>
public sealed class RealmRelay(RealmAdminClient realms, SiteGatePublisher publisher, IPluginContext context, TenancyDbContext tenancy)
{
    public static readonly IResult Linked = Results.Conflict(new
    {
        error = "This environment's sites sign people in through another environment's users. Manage them there.",
        code = "environment_users_linked",
    });

    private Guid? _realmTenant;

    public async Task<Guid> RealmTenantAsync(CancellationToken ct) =>
        _realmTenant ??= await EnvironmentRealm.OfAsync(tenancy, context.TenantId, ct);

    /// <param name="then">Run after identity said yes, with what it answered.</param>
    public async Task<IResult> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, Func<string, Task>? then = null)
    {
        var realmTenant = await RealmTenantAsync(ct);
        // Every write refused, "view as" too: a token for the other environment's person is one
        // that environment would accept, and nobody there granted this member that.
        if (realmTenant != context.EnvironmentId && method != HttpMethod.Get)
        {
            return Linked;
        }
        var response = await realms.SendAsync(realmTenant, method, path, body, ct);
        try
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound && await realms.GetRealmAsync(realmTenant, ct) is null
                && await publisher.SyncRealmAsync(realmTenant, null, ct) is not null)
            {
                response.Dispose();
                response = await realms.SendAsync(realmTenant, method, path, body, ct);
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
