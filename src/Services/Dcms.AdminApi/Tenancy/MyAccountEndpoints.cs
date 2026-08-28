using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// The caller's own membership of the platform, independent of any selected
/// tenant: which workspaces they belong to, and detaching from all of them ahead of
/// deleting their account.
///
/// Account deletion is split across two services on purpose. admin-api owns the
/// tenancy rules and every tenant-scoped row, so it decides whether detaching is
/// allowed and does the cleanup; identity owns the user record and refuses to
/// delete it while any membership remains (see Dcms.Identity's
/// <c>DELETE /account/api/me</c>). Neither writes the other's tables, and the
/// dangerous half — destroying the login — cannot run before the safe half.
///
/// All of these run without an ambient tenant, so query filters are bypassed and
/// tenant ids are matched explicitly.
/// </summary>
public static class MyAccountEndpoints
{
    public static IEndpointRouteBuilder MapMyAccountEndpoints(this IEndpointRouteBuilder app)
    {
        // Everything the account page needs to explain what stands between the user
        // and deleting their account.
        app.MapGet("/api/admin/me/account", async (
            CurrentUser me, TenancyDbContext db, CancellationToken ct) =>
        {
            var userId = me.RequireUserId();
            var memberships = await LoadMembershipsAsync(db, userId, ct);
            return Results.Ok(new
            {
                workspaces = memberships.Select(m => new
                {
                    m.TenantId,
                    m.Slug,
                    m.Name,
                    m.IsOwner,
                    m.IsSoleOwner,
                }),
                // The one condition that blocks deletion. Everything else is cleanup.
                canDelete = memberships.All(m => !m.IsSoleOwner),
            });
        }).RequireAuthorization().AllowNonMemberTenant(AllowNonMemberTenantAttribute.SelfScoped);

        // Detach the account from the platform: leave every workspace and drop the
        // per-user rows that only exist because this user was working here. The login
        // itself is deleted afterwards by identity, which refuses until this has run.
        //
        // Idempotent: running it again on an already-detached account is a no-op, so a
        // client that fails between this call and identity's can simply retry both.
        app.MapDelete("/api/admin/me", async (
            CurrentUser me, TenancyDbContext db, SitesDbContext sites, AiDbContext ai,
            TenancyPermissionResolver permissions, IEventPublisher events,
            IAuditRecorder audit, AuditScope scope, CancellationToken ct) =>
        {
            var userId = me.RequireUserId();
            var memberships = await LoadMembershipsAsync(db, userId, ct);

            var blocking = memberships.Where(m => m.IsSoleOwner).ToList();
            if (blocking.Count > 0)
            {
                return Results.Conflict(new
                {
                    error = "Transfer or delete the workspaces you own before deleting your account.",
                    workspaces = blocking.Select(m => new { m.TenantId, m.Slug, m.Name }),
                });
            }

            // Every statement below is set-based, and the departures span several tenants.
            // One record per tenant, because each tenant's log has to show its own member
            // leaving — a summary on the ambient tenant would put every workspace's departure
            // in whichever one happened to be selected in the browser.
            using var _ = scope.SuppressBulkCapture();

            foreach (var membership in memberships)
            {
                audit.Record(AuditActions.MemberLeft)
                    .InTenant(membership.TenantId)
                    .For("membership", membership.MembershipId, me.Email)
                    .About(userId)
                    .With("workspace", membership.Slug)
                    .With("reason", "account-deleted");

                await RemoveMembershipAsync(db, membership.MembershipId, ct);
                await permissions.InvalidateAsync(membership.TenantId, userId, ct);
                await events.PublishAsync(Subjects.MembershipChanged,
                    new MembershipChanged(Guid.NewGuid(), DateTimeOffset.UtcNow, membership.TenantId, userId), ct);
            }

            // Pending invitations addressed to them are now dead letters.
            var email = me.Email;
            if (!string.IsNullOrWhiteSpace(email))
            {
                var normalized = email.Trim().ToLowerInvariant();
                await db.Invitations.IgnoreQueryFilters()
                    .Where(i => i.Email == normalized && i.AcceptedAt == null)
                    .ExecuteDeleteAsync(ct);
            }

            // Per-user leftovers in other schemas: unsaved IDE working copies and the
            // user's own AI provider settings (which hold an encrypted API key).
            var drafts = await sites.Drafts.IgnoreQueryFilters()
                .Where(d => d.UserId == userId).ExecuteDeleteAsync(ct);
            var aiSettings = await ai.UserSettings
                .Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);

            // The platform-scope counterpart: the tenants each saw their own member go, but
            // nothing above says the account itself was taken apart.
            audit.Declare(AuditActions.AccountDetached)
                .Platform()
                .About(userId)
                .As(AuditCategory.Auth, AuditSeverity.Notice)
                .With("workspaces_left", memberships.Count)
                .With("drafts_deleted", drafts)
                .With("ai_settings_deleted", aiSettings);

            return Results.Ok(new
            {
                workspacesLeft = memberships.Count,
                draftsDeleted = drafts,
                aiSettingsDeleted = aiSettings,
            });
        }).RequireAuthorization()
          .AllowNonMemberTenant(AllowNonMemberTenantAttribute.SelfScoped)
          .WithAudit(AuditActions.AccountDetached, category: AuditCategory.Auth);

        return app;
    }

    private sealed record MembershipView(
        Guid MembershipId, Guid TenantId, string Slug, string Name, bool IsOwner, bool IsSoleOwner);

    /// <summary>
    /// The caller's memberships with the ownership facts the delete rule needs.
    /// "Sole owner" is computed per tenant rather than trusted from a flag, because it
    /// is the single condition that can leave a workspace unreachable.
    /// </summary>
    private static async Task<List<MembershipView>> LoadMembershipsAsync(
        TenancyDbContext db, Guid userId, CancellationToken ct)
    {
        var mine = await db.Memberships.IgnoreQueryFilters()
            .Where(m => m.UserId == userId)
            .Select(m => new
            {
                m.Id,
                m.TenantId,
                IsOwner = m.Roles.Any(r => r.Role!.IsSystem && r.Role.Name == TenantProvisioning.OwnerRole),
            })
            .ToListAsync(ct);
        if (mine.Count == 0)
        {
            return [];
        }

        var tenantIds = mine.Select(m => m.TenantId).ToList();
        var ownerCounts = await db.Memberships.IgnoreQueryFilters()
            .Where(m => tenantIds.Contains(m.TenantId)
                        && m.Roles.Any(r => r.Role!.IsSystem && r.Role.Name == TenantProvisioning.OwnerRole))
            .GroupBy(m => m.TenantId)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TenantId, x => x.Count, ct);

        var keys = tenantIds.Select(id => id.ToString()).ToList();
        var tenants = await db.Tenants
            .Where(t => keys.Contains(t.Id))
            .ToDictionaryAsync(t => t.TenantId, ct);

        return mine
            .Where(m => tenants.ContainsKey(m.TenantId))
            .Select(m => new MembershipView(
                m.Id,
                m.TenantId,
                tenants[m.TenantId].Identifier,
                tenants[m.TenantId].Name ?? tenants[m.TenantId].Identifier,
                m.IsOwner,
                m.IsOwner && ownerCounts.GetValueOrDefault(m.TenantId) <= 1))
            .ToList();
    }

    /// <summary>
    /// Deletes a membership and its role assignments. Role rows go first: they are a
    /// separate table with no cascade configured, so removing the membership alone
    /// would strand them.
    /// </summary>
    private static async Task RemoveMembershipAsync(TenancyDbContext db, Guid membershipId, CancellationToken ct)
    {
        await db.MemberRoles.IgnoreQueryFilters()
            .Where(r => r.MembershipId == membershipId).ExecuteDeleteAsync(ct);
        await db.Memberships.IgnoreQueryFilters()
            .Where(m => m.Id == membershipId).ExecuteDeleteAsync(ct);
    }
}
