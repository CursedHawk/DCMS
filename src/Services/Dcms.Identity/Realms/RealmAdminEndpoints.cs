using System.Net.Mail;
using System.Text.RegularExpressions;
using Dcms.Identity.Data;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Messaging.Email;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Dcms.Identity.Realms;

public sealed record RealmUpsert(string Slug, string Name, IReadOnlyList<string>? Hosts);

/// <param name="ClientReady">False until identity holds the edge's master secret: the realm exists, but no site can sign anyone in yet.</param>
public sealed record RealmInfo(Guid TenantId, string Slug, string Name, IReadOnlyList<string> Hosts, string ClientId, bool ClientReady);

public sealed record RealmUserInfo(
    Guid Id, string Email, string? DisplayName, string Status, IReadOnlyList<Guid> Groups,
    bool HasPassword, bool LockedOut, DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt);

public sealed record RealmUserPage(IReadOnlyList<RealmUserInfo> Items, int Total, int Page, int PageSize);

public sealed record RealmInvite(string Email, string? DisplayName, IReadOnlyList<Guid>? Groups);

/// <param name="InviteUrl">The link the invitation email carries, for an administrator to pass on when mail does not arrive.</param>
public sealed record RealmInviteResult(RealmUserInfo User, string InviteUrl);

/// <param name="Status"><c>active</c> or <c>disabled</c>; an invited account becomes active only by accepting.</param>
public sealed record RealmUserPatch(string? DisplayName, string? Status);

public sealed record RealmGroupWrite(string Name, string? Description);

public sealed record RealmGroupInfo(Guid Id, string Name, string? Description, int Members);

/// <summary>
/// Identity's realm admin API (ADR 0022), for admin-api only (scope <c>dcms.realms</c>): a
/// tenant's realm, its users and groups, and its OIDC client. The tenant's console reaches it
/// through the User Authentication plugin, which checks the member's permissions first; here the
/// caller is trusted to have done so, and every call names the one tenant it acts on.
/// </summary>
/// <summary>
/// Marks an endpoint as reached by another DCMS service with a client-credentials token whose
/// scope is checked — the gate the permission coverage guard recognises by this name.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AllowServicePrincipalAttribute(string scope) : Attribute
{
    public string Scope { get; } = scope;
}

public static partial class RealmAdminEndpoints
{
    public const string PolicyName = "RealmAdmin";
    public const int MaxHosts = 50;

    public static IEndpointRouteBuilder MapRealmAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // A service principal (admin-api) holding dcms.realms, checked by the RealmAdmin policy; the
        // member's own permission is checked by the User Authentication plugin before it calls.
        var realms = app.MapGroup("/api/realms/{tenantId:guid}").RequireAuthorization(PolicyName)
            .WithMetadata(new AllowServicePrincipalAttribute(DcmsOAuth.Scopes.Realms));

        realms.MapGet("", async (Guid tenantId, RealmStore store, IConfiguration configuration, IOpenIddictApplicationManager apps, CancellationToken ct) =>
            await store.FindRealmAsync(tenantId, ct) is { } realm ? Results.Ok(await InfoAsync(realm, configuration, apps, ct)) : Results.NotFound());

        // Created when the plugin is enabled, and kept in step with the tenant's slug, name and
        // site hostnames, which are the client's redirect URIs.
        realms.MapPut("", async (Guid tenantId, RealmUpsert body, IdentityDbContext db, IConfiguration configuration,
            IOpenIddictApplicationManager apps, IAuditRecorder audit, CancellationToken ct) =>
        {
            var hosts = (body.Hosts ?? []).Select(h => h.Trim().ToLowerInvariant()).Distinct().ToList();
            // The name goes into email subjects as well as pages: no control characters (header injection).
            if (!SlugPattern().IsMatch(body.Slug ?? "") || string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 200
                || body.Name.Any(char.IsControl)
                || hosts.Count > MaxHosts || hosts.Any(h => !HostPattern().IsMatch(h)))
            {
                return Results.BadRequest(new { error = "A realm needs a slug, a name of up to 200 characters, and up to 50 valid hostnames." });
            }
            if (await db.Realms.AnyAsync(r => r.Slug == body.Slug && r.TenantId != tenantId, ct))
            {
                return Results.Conflict(new { error = $"The slug '{body.Slug}' belongs to another realm." });
            }
            var realm = await db.Realms.FirstOrDefaultAsync(r => r.TenantId == tenantId, ct);
            if (realm is null)
            {
                realm = new Realm { TenantId = tenantId };
                db.Realms.Add(realm);
            }
            realm.Slug = body.Slug!;
            realm.Name = body.Name.Trim();
            realm.Hosts = hosts;
            realm.UpdatedAt = DateTimeOffset.UtcNow;
            audit.Declared?.InTenant(tenantId).With("hosts", hosts.Count);
            await db.SaveChangesAsync(ct);
            await RealmClients.EnsureAsync(apps, configuration, realm, ct);
            return Results.Ok(await InfoAsync(realm, configuration, apps, ct));
        }).WithAudit(AuditActions.RealmUpserted, "realm", AuditCategory.Auth);

        // The tenant is going: its users, groups, logins, client and every token it issued.
        realms.MapDelete("", async (Guid tenantId, IdentityDbContext db, IOpenIddictApplicationManager apps, IAuditRecorder audit, CancellationToken ct) =>
        {
            audit.Declared?.InTenant(tenantId);
            await RealmClients.DeleteAsync(apps, tenantId, ct);
            // Cascades to users, their logins and memberships, and the groups.
            var deleted = await db.Realms.Where(r => r.TenantId == tenantId).ExecuteDeleteAsync(ct);
            return deleted == 0 ? Results.NotFound() : Results.NoContent();
        }).WithAudit(AuditActions.RealmDeleted, "realm", AuditCategory.Auth);

        // ---- users ----

        realms.MapGet("/users", async (Guid tenantId, string? search, int? page, int? pageSize, IdentityDbContext db, RealmStore store, CancellationToken ct) =>
        {
            var size = Math.Clamp(pageSize ?? 50, 1, 100);
            var number = Math.Max(page ?? 1, 1);
            var users = db.RealmUsers.AsNoTracking().Where(u => u.TenantId == tenantId);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var pattern = $"%{search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
                users = users.Where(u => EF.Functions.ILike(u.Email, pattern) || (u.DisplayName != null && EF.Functions.ILike(u.DisplayName, pattern)));
            }
            var total = await users.CountAsync(ct);
            var items = await users.OrderBy(u => u.Email).Skip((number - 1) * size).Take(size).ToListAsync(ct);
            var groups = await GroupsOfAsync(db, tenantId, items.Select(u => u.Id).ToList(), ct);
            return Results.Ok(new RealmUserPage(items.Select(u => Info(u, groups, store)).ToList(), total, number, size));
        });

        realms.MapGet("/users/{userId:guid}", async (Guid tenantId, Guid userId, IdentityDbContext db, RealmStore store, CancellationToken ct) =>
            await store.FindUserAsync(tenantId, userId, ct) is { } user
                ? Results.Ok(Info(user, await GroupsOfAsync(db, tenantId, [user.Id], ct), store))
                : Results.NotFound());

        realms.MapPost("/users/invite", async (Guid tenantId, RealmInvite body, HttpContext http, IdentityDbContext db, RealmStore store,
            IEmailQueue email, IConfiguration configuration, ILoggerFactory logs, IAuditRecorder audit, CancellationToken ct) =>
        {
            if (await store.FindRealmAsync(tenantId, ct) is not { } realm)
            {
                return Results.NotFound();
            }
            if (!ValidEmail(body.Email) || body.DisplayName?.Length > 200 || body.DisplayName?.Any(char.IsControl) == true)
            {
                return Results.BadRequest(new { error = "An invitation needs a valid email address and a name of up to 200 characters." });
            }
            if (await store.FindByEmailAsync(tenantId, body.Email, ct) is not null)
            {
                return Results.Conflict(new { error = "Someone with this email already has an account here." });
            }
            var groupIds = (body.Groups ?? []).Distinct().ToList();
            if (groupIds.Count > 0 && await db.RealmGroups.CountAsync(g => g.TenantId == tenantId && groupIds.Contains(g.Id), ct) != groupIds.Count)
            {
                return Results.BadRequest(new { error = "A group named in the invitation does not exist." });
            }
            var user = new RealmUser
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Email = body.Email.Trim(),
                NormalizedEmail = RealmStore.Normalize(body.Email),
                DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName.Trim(),
                Status = RealmUserStatus.Invited,
            };
            db.RealmUsers.Add(user);
            db.RealmGroupMembers.AddRange(groupIds.Select(g => new RealmGroupMember { TenantId = tenantId, GroupId = g, UserId = user.Id }));
            audit.Declared?.InTenant(tenantId).About(user.Id).With("email", user.Email);
            await db.SaveChangesAsync(ct);
            var link = await InviteAsync(http, realm, user, store, email, configuration, logs, ct);
            return Results.Created($"/api/realms/{tenantId}/users/{user.Id}",
                new RealmInviteResult(Info(user, new Dictionary<Guid, List<Guid>> { [user.Id] = groupIds }, store), link));
        }).WithAudit(AuditActions.RealmUserInvited, "realm_user", AuditCategory.Auth);

        realms.MapPost("/users/{userId:guid}/invite", async (Guid tenantId, Guid userId, HttpContext http, RealmStore store,
            IEmailQueue email, IConfiguration configuration, ILoggerFactory logs, IAuditRecorder audit, CancellationToken ct) =>
        {
            if (await store.FindRealmAsync(tenantId, ct) is not { } realm || await store.FindUserAsync(tenantId, userId, ct) is not { } user)
            {
                return Results.NotFound();
            }
            if (user.Status != RealmUserStatus.Invited)
            {
                return Results.Conflict(new { error = "This person has already accepted their invitation." });
            }
            audit.Declared?.InTenant(tenantId).About(user.Id);
            return Results.Ok(new { inviteUrl = await InviteAsync(http, realm, user, store, email, configuration, logs, ct) });
        }).WithAudit(AuditActions.RealmUserInviteResent, "realm_user", AuditCategory.Auth);

        realms.MapPatch("/users/{userId:guid}", async (Guid tenantId, Guid userId, RealmUserPatch body, IdentityDbContext db, RealmStore store,
            IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, IAuditRecorder audit, CancellationToken ct) =>
        {
            if (await store.FindUserAsync(tenantId, userId, ct) is not { } user)
            {
                return Results.NotFound();
            }
            if (body.DisplayName is { } name)
            {
                if (name.Length > 200)
                {
                    return Results.BadRequest(new { error = "A name is at most 200 characters." });
                }
                user.DisplayName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }
            var endSessions = false;
            if (body.Status is { } status)
            {
                switch (status)
                {
                    case "disabled" when user.Status != RealmUserStatus.Disabled:
                        user.Status = RealmUserStatus.Disabled;
                        RealmStore.Restamp(user);
                        endSessions = true;
                        break;
                    case "active" when user.Status == RealmUserStatus.Disabled:
                        // Back to where it was: an account that never set a password is still waiting for its invitation.
                        user.Status = user.PasswordHash is null ? RealmUserStatus.Invited : RealmUserStatus.Active;
                        user.LockoutEnd = null;
                        user.AccessFailedCount = 0;
                        break;
                    case "active" or "disabled":
                        break;
                    default:
                        return Results.BadRequest(new { error = "Status is active or disabled." });
                }
            }
            audit.Declared?.InTenant(tenantId).About(user.Id).With("status", user.Status.ToString());
            await db.SaveChangesAsync(ct);
            if (endSessions)
            {
                await UserSessionRevoker.RevokeSubjectAsync(tokens, authorizations, user.Id.ToString(), ct);
            }
            return Results.Ok(Info(user, await GroupsOfAsync(db, tenantId, [user.Id], ct), store));
        }).WithAudit(AuditActions.RealmUserUpdated, "realm_user", AuditCategory.Auth);

        // Ends every session the user holds — cookies at the next request, tokens at once.
        realms.MapPost("/users/{userId:guid}/sign-out", async (Guid tenantId, Guid userId, IdentityDbContext db, RealmStore store,
            IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, IAuditRecorder audit, CancellationToken ct) =>
        {
            if (await store.FindUserAsync(tenantId, userId, ct) is not { } user)
            {
                return Results.NotFound();
            }
            RealmStore.Restamp(user);
            audit.Declared?.InTenant(tenantId).About(user.Id);
            await db.SaveChangesAsync(ct);
            await UserSessionRevoker.RevokeSubjectAsync(tokens, authorizations, user.Id.ToString(), ct);
            return Results.NoContent();
        }).WithAudit(AuditActions.RealmUserSignedOut, "realm_user", AuditCategory.Auth);

        realms.MapDelete("/users/{userId:guid}", async (Guid tenantId, Guid userId, IdentityDbContext db,
            IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, IAuditRecorder audit, CancellationToken ct) =>
        {
            audit.Declared?.InTenant(tenantId).About(userId);
            var deleted = await db.RealmUsers.Where(u => u.TenantId == tenantId && u.Id == userId).ExecuteDeleteAsync(ct);
            if (deleted == 0)
            {
                return Results.NotFound();
            }
            await UserSessionRevoker.RevokeSubjectAsync(tokens, authorizations, userId.ToString(), ct);
            return Results.NoContent();
        }).WithAudit(AuditActions.RealmUserDeleted, "realm_user", AuditCategory.Auth);

        // ---- groups ----

        realms.MapGet("/groups", async (Guid tenantId, IdentityDbContext db, CancellationToken ct) =>
            Results.Ok(await db.RealmGroups.AsNoTracking().Where(g => g.TenantId == tenantId).OrderBy(g => g.Name)
                .Select(g => new RealmGroupInfo(g.Id, g.Name, g.Description, db.RealmGroupMembers.Count(m => m.GroupId == g.Id)))
                .ToListAsync(ct)));

        realms.MapPost("/groups", async (Guid tenantId, RealmGroupWrite body, IdentityDbContext db, RealmStore store, IAuditRecorder audit, CancellationToken ct) =>
        {
            if (await store.FindRealmAsync(tenantId, ct) is null)
            {
                return Results.NotFound();
            }
            if (Invalid(body) is { } problem)
            {
                return problem;
            }
            if (await db.RealmGroups.AnyAsync(g => g.TenantId == tenantId && g.Name == body.Name.Trim(), ct))
            {
                return Results.Conflict(new { error = $"There is already a group called '{body.Name.Trim()}'." });
            }
            var group = new RealmGroup { Id = Guid.NewGuid(), TenantId = tenantId, Name = body.Name.Trim(), Description = body.Description?.Trim() };
            db.RealmGroups.Add(group);
            audit.Declared?.InTenant(tenantId).About(group.Id).With("name", group.Name);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/realms/{tenantId}/groups/{group.Id}", new RealmGroupInfo(group.Id, group.Name, group.Description, 0));
        }).WithAudit(AuditActions.RealmGroupCreated, "realm_group", AuditCategory.Auth);

        realms.MapPut("/groups/{groupId:guid}", async (Guid tenantId, Guid groupId, RealmGroupWrite body, IdentityDbContext db, IAuditRecorder audit, CancellationToken ct) =>
        {
            if (await db.RealmGroups.FirstOrDefaultAsync(g => g.TenantId == tenantId && g.Id == groupId, ct) is not { } group)
            {
                return Results.NotFound();
            }
            if (Invalid(body) is { } problem)
            {
                return problem;
            }
            if (await db.RealmGroups.AnyAsync(g => g.TenantId == tenantId && g.Id != groupId && g.Name == body.Name.Trim(), ct))
            {
                return Results.Conflict(new { error = $"There is already a group called '{body.Name.Trim()}'." });
            }
            group.Name = body.Name.Trim();
            group.Description = body.Description?.Trim();
            audit.Declared?.InTenant(tenantId).About(group.Id).With("name", group.Name);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new RealmGroupInfo(group.Id, group.Name, group.Description,
                await db.RealmGroupMembers.CountAsync(m => m.GroupId == group.Id, ct)));
        }).WithAudit(AuditActions.RealmGroupUpdated, "realm_group", AuditCategory.Auth);

        realms.MapDelete("/groups/{groupId:guid}", async (Guid tenantId, Guid groupId, IdentityDbContext db, IAuditRecorder audit, CancellationToken ct) =>
        {
            audit.Declared?.InTenant(tenantId).About(groupId);
            // Cascades to its memberships.
            return await db.RealmGroups.Where(g => g.TenantId == tenantId && g.Id == groupId).ExecuteDeleteAsync(ct) == 0
                ? Results.NotFound()
                : Results.NoContent();
        }).WithAudit(AuditActions.RealmGroupDeleted, "realm_group", AuditCategory.Auth);

        realms.MapPut("/groups/{groupId:guid}/members/{userId:guid}", async (Guid tenantId, Guid groupId, Guid userId, IdentityDbContext db,
            IAuditRecorder audit, CancellationToken ct) =>
        {
            if (!await db.RealmGroups.AnyAsync(g => g.TenantId == tenantId && g.Id == groupId, ct)
                || !await db.RealmUsers.AnyAsync(u => u.TenantId == tenantId && u.Id == userId, ct))
            {
                return Results.NotFound();
            }
            audit.Declared?.InTenant(tenantId).About(userId).With("group", groupId);
            if (!await db.RealmGroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId, ct))
            {
                db.RealmGroupMembers.Add(new RealmGroupMember { TenantId = tenantId, GroupId = groupId, UserId = userId });
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        }).WithAudit(AuditActions.RealmGroupMemberAdded, "realm_group", AuditCategory.Auth);

        realms.MapDelete("/groups/{groupId:guid}/members/{userId:guid}", async (Guid tenantId, Guid groupId, Guid userId, IdentityDbContext db,
            IAuditRecorder audit, CancellationToken ct) =>
        {
            audit.Declared?.InTenant(tenantId).About(userId).With("group", groupId);
            await db.RealmGroupMembers.Where(m => m.TenantId == tenantId && m.GroupId == groupId && m.UserId == userId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        }).WithAudit(AuditActions.RealmGroupMemberRemoved, "realm_group", AuditCategory.Auth);

        return app;
    }

    private static async Task<string> InviteAsync(HttpContext http, Realm realm, RealmUser user, RealmStore store, IEmailQueue email,
        IConfiguration configuration, ILoggerFactory logs, CancellationToken ct)
    {
        var link = $"{RealmAccountEndpoints.PublicOrigin(configuration)}/realm/{Uri.EscapeDataString(realm.Slug)}/invite?token={Uri.EscapeDataString(store.InviteToken(user))}";
        await RealmAccountEndpoints.SendAsync(email, logs, user.Email, $"You're invited to {realm.Name}",
            RealmAccountEndpoints.LinkEmail($"You're invited to {realm.Name}",
                $"You have been given an account for {realm.Name}'s sites. Choose a password to start. The link is valid for seven days.",
                "Accept invitation", link),
            "realm-invite", ct);
        return link;
    }

    private static async Task<RealmInfo> InfoAsync(Realm realm, IConfiguration configuration, IOpenIddictApplicationManager apps, CancellationToken ct) =>
        new(realm.TenantId, realm.Slug, realm.Name, realm.Hosts, RealmClients.IdFor(realm.TenantId),
            !string.IsNullOrWhiteSpace(configuration[RealmClients.SecretSetting])
            && await apps.FindByClientIdAsync(RealmClients.IdFor(realm.TenantId), ct) is not null);

    private static RealmUserInfo Info(RealmUser user, IReadOnlyDictionary<Guid, List<Guid>> groups, RealmStore store) => new(
        user.Id, user.Email, user.DisplayName, user.Status.ToString().ToLowerInvariant(),
        groups.GetValueOrDefault(user.Id) ?? [], user.PasswordHash is not null, store.IsLockedOut(user), user.CreatedAt, user.LastSignInAt);

    private static async Task<Dictionary<Guid, List<Guid>>> GroupsOfAsync(IdentityDbContext db, Guid tenantId, List<Guid> userIds, CancellationToken ct) =>
        (await db.RealmGroupMembers.AsNoTracking().Where(m => m.TenantId == tenantId && userIds.Contains(m.UserId)).ToListAsync(ct))
            .GroupBy(m => m.UserId).ToDictionary(g => g.Key, g => g.Select(m => m.GroupId).ToList());

    private static IResult? Invalid(RealmGroupWrite body) =>
        string.IsNullOrWhiteSpace(body.Name) || body.Name.Trim().Length > 120 || body.Description?.Length > 1000
            ? Results.BadRequest(new { error = "A group needs a name of up to 120 characters; a description is at most 1000." })
            : null;

    private static bool ValidEmail(string? email) =>
        email is { Length: > 3 and <= 256 } && MailAddress.TryCreate(email.Trim(), out var parsed) && parsed.Address == email.Trim();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex SlugPattern();

    // A DNS hostname: labels of letters, digits and hyphens, at least one dot.
    [GeneratedRegex(@"^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,61}[a-z0-9]$")]
    private static partial Regex HostPattern();
}
