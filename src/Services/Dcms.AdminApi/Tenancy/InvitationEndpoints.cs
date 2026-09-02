using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using System.Net;
using System.Security.Cryptography;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.AdminApi.Notifications;
using Dcms.Shared.Data.Notifications;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Messaging.Email;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy;

public static class InvitationEndpoints
{
    /// <summary>How long a freshly issued invitation stays acceptable.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        // List invitations that have not been accepted, newest first. Expired ones
        // are kept in the list (flagged) rather than hidden: an admin looking at
        // "why hasn't this person joined" needs to see the dead invite in order to
        // resend or revoke it.
        app.MapGet("/api/admin/invitations", async (TenancyDbContext db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var rows = await db.Invitations
                .Where(i => i.AcceptedAt == null)
                .OrderByDescending(i => i.CreatedAt)
                .Select(i => new
                {
                    id = i.Id,
                    email = i.Email,
                    roleIdsCsv = i.RoleIdsCsv,
                    createdAt = i.CreatedAt,
                    expiresAt = i.ExpiresAt,
                })
                .ToListAsync(ct);

            return Results.Ok(rows.Select(r => new
            {
                r.id,
                r.email,
                roleIds = ParseRoleIds(r.roleIdsCsv),
                r.createdAt,
                r.expiresAt,
                expired = r.expiresAt <= now,
            }));
        }).RequirePermission(PlatformPermissions.MembersManage);

        // Create an invitation (tenant-scoped). The raw token is returned once so
        // the inviter can copy the link, and is also emailed to the invitee.
        app.MapPost("/api/admin/invitations", async (
            CreateInvitationRequest body, HttpContext http, TenancyDbContext db, ITenantContext tenant,
            CurrentUser me, IEmailQueue emailQueue, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var email = body.Email?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email))
            {
                return Results.BadRequest(new { error = "Email required." });
            }

            var tenantId = tenant.TenantId!.Value;

            // Already a member: inviting again would create an invitation that,
            // once accepted, is a no-op — say so instead.
            if (await db.Memberships.AnyAsync(m => m.Email == email, ct))
            {
                return Results.BadRequest(new { error = "That address is already a member of this tenant." });
            }

            // Re-inviting an address that already has a live invitation replaces it,
            // so the member list never shows the same person twice and the older
            // link stops working.
            var existing = await db.Invitations
                .Where(i => i.Email == email && i.AcceptedAt == null)
                .ToListAsync(ct);
            if (existing.Count > 0)
            {
                db.Invitations.RemoveRange(existing);
            }

            var token = GenerateToken();
            var invitation = new Invitation
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Email = email,
                TokenHash = HashToken(token),
                RoleIdsCsv = string.Join(',', body.RoleIds ?? []),
                ExpiresAt = DateTimeOffset.UtcNow.Add(Lifetime),
            };
            db.Invitations.Add(invitation);
            await db.SaveChangesAsync(ct);

            var link = BuildAcceptLink(http, token);
            await SendAsync(emailQueue, loggerFactory, db, invitation, link, me, ct);

            return Results.Created($"/api/admin/invitations/{invitation.Id}", new
            {
                invitationId = invitation.Id,
                email = invitation.Email,
                token,
                link,
                expiresAt = invitation.ExpiresAt,
            });
        }).RequirePermission(PlatformPermissions.MembersManage).WithAudit(AuditActions.MemberInvited, "invitation");

        // Resend: mint a fresh token and push the expiry out. The old link dies with
        // the old hash, which is what an admin resending after "I lost the email"
        // expects — and means a leaked stale link cannot be redeemed later.
        app.MapPost("/api/admin/invitations/{id:guid}/resend", async (
            Guid id, HttpContext http, TenancyDbContext db, CurrentUser me,
            IEmailQueue emailQueue, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.Id == id, ct);
            if (invitation is null)
            {
                return Results.NotFound();
            }
            if (invitation.AcceptedAt is not null)
            {
                return Results.BadRequest(new { error = "That invitation has already been accepted." });
            }

            var token = GenerateToken();
            invitation.TokenHash = HashToken(token);
            invitation.ExpiresAt = DateTimeOffset.UtcNow.Add(Lifetime);
            // Resending gives the invitation a fresh deadline, so the "it lapsed" report is
            // owed again. Without this the sweeper, which skips anything already stamped,
            // would never mention this invitation again however many times it was resent.
            invitation.ExpiredNotifiedAt = null;
            await db.SaveChangesAsync(ct);

            var link = BuildAcceptLink(http, token);
            await SendAsync(emailQueue, loggerFactory, db, invitation, link, me, ct);

            return Results.Ok(new
            {
                invitationId = invitation.Id,
                email = invitation.Email,
                token,
                link,
                expiresAt = invitation.ExpiresAt,
            });
        }).RequirePermission(PlatformPermissions.MembersManage).WithAudit(AuditActions.MemberInviteResent, "invitation");

        // Revoke. Deleting the row is the revocation: acceptance looks the token
        // hash up, so with the row gone an outstanding link is simply invalid.
        app.MapDelete("/api/admin/invitations/{id:guid}", async (
            Guid id, TenancyDbContext db, CancellationToken ct) =>
        {
            var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.Id == id, ct);
            if (invitation is null)
            {
                return Results.NotFound();
            }
            db.Invitations.Remove(invitation);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.MembersManage).WithAudit(AuditActions.MemberInviteRevoked, "invitation");

        // Accept an invitation. Authenticated; the caller becomes a member of the
        // invited tenant with the invitation's roles. Operates across tenants, so
        // query filters are bypassed and tenant ids are set explicitly.
        app.MapPost("/api/admin/invitations/accept", async (
            AcceptInvitationRequest body, CurrentUser me, TenancyDbContext db,
            INotificationPublisher notifications,
            TenancyPermissionResolver permissions, IEventPublisher events, CancellationToken ct) =>
        {
            var userId = me.RequireUserId();
            var hash = HashToken(body.Token);
            var invitation = await db.Invitations.IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
            if (invitation is null || !invitation.IsPending)
            {
                return Results.BadRequest(new { error = "Invitation is invalid or expired." });
            }

            var tenantId = invitation.TenantId;
            var membership = await db.Memberships.IgnoreQueryFilters()
                .Include(m => m.Roles)
                .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId, ct);

            if (membership is null)
            {
                membership = new TenantMembership
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    UserId = userId,
                    Email = me.Email ?? invitation.Email,
                };
                db.Memberships.Add(membership);
            }

            var roleIds = ParseRoleIds(invitation.RoleIdsCsv);

            var validRoleIds = await db.TenantRoles.IgnoreQueryFilters()
                .Where(r => r.TenantId == tenantId && roleIds.Contains(r.Id))
                .Select(r => r.Id)
                .ToListAsync(ct);

            foreach (var roleId in validRoleIds)
            {
                if (membership.Roles.All(r => r.TenantRoleId != roleId))
                {
                    membership.Roles.Add(new MemberRole
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        MembershipId = membership.Id,
                        TenantRoleId = roleId,
                    });
                }
            }

            invitation.AcceptedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await permissions.InvalidateAsync(tenantId, userId, ct);
            await events.PublishAsync(Subjects.MembershipChanged,
                new MembershipChanged(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, userId), ct);

            // Raised here rather than off membership.changed, because that event carries only
            // (tenant, user) — it cannot say which invitation was accepted or by which email,
            // and it also fires for role edits and removals. RaiseAsync swallows its own
            // failures, so a notification problem cannot cost someone their membership.
            await notifications.RaiseAsync(new NotificationRequest(
                TenantId: tenantId,
                Kind: NotificationKinds.InvitationAccepted,
                Severity: NotificationSeverity.Success,
                RequiredPermission: PlatformPermissions.MembersManage,
                TitleKey: NotificationKinds.TitleKey(NotificationKinds.InvitationAccepted),
                BodyKey: NotificationKinds.BodyKey(NotificationKinds.InvitationAccepted),
                DedupeKey: $"invitation.accepted:{invitation.Id:N}",
                Params: new { email = invitation.Email },
                LinkPath: "/members",
                ResourceType: "invitation",
                ResourceId: invitation.Id,
                // The accepter is the actor: they know they just joined, so they get the bell
                // entry without a toast, while the admins who invited them get both.
                ActorUserId: userId), ct);

            // The SPA selects tenants by slug (the X-Dcms-Tenant header), and an
            // invitee accepting their first invitation has no tenant selected at
            // all — return enough for the page to switch them straight into the
            // workspace they just joined.
            var tenantKey = tenantId.ToString();
            var joined = await db.Tenants
                .Where(t => t.Id == tenantKey)
                .Select(t => new { t.Identifier, t.Name })
                .FirstOrDefaultAsync(ct);

            return Results.Ok(new
            {
                tenantId,
                membershipId = membership.Id,
                tenantSlug = joined?.Identifier,
                tenantName = joined?.Name,
            });
        }).RequireAuthorization()
          .AllowNonMemberTenant("Accepting an invitation is how the caller becomes a member. "
                              + "The tenant comes from the invitation token, not the header, "
                              + "which this endpoint never reads.")
          .WithAudit(AuditActions.MemberInviteAccepted, "invitation");

        return app;
    }

    private static List<Guid> ParseRoleIds(string csv) => csv
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
        .Where(g => g != Guid.Empty)
        .ToList();

    /// <summary>
    /// Absolute link to the SPA's accept page. The admin SPA and admin-api share one
    /// public origin behind Caddy (see infra/caddy/Caddyfile), so the incoming
    /// request's scheme+host is the SPA's origin.
    /// </summary>
    private static string BuildAcceptLink(HttpContext http, string token) =>
        $"{http.Request.Scheme}://{http.Request.Host}/invite/accept?token={Uri.EscapeDataString(token)}";

    /// <summary>
    /// Queues the invitation email. Mail is best-effort: the invitation is already
    /// persisted and the raw link is returned to the inviter, so a queue outage must
    /// not fail the request — it just means the admin has to share the link manually.
    /// </summary>
    private static async Task SendAsync(
        IEmailQueue emailQueue, ILoggerFactory loggerFactory, TenancyDbContext db,
        Invitation invitation, string link, CurrentUser me, CancellationToken ct)
    {
        // The tenants table is not tenant-scoped, so no filter to bypass here.
        var tenantKey = invitation.TenantId.ToString();
        var tenantName = await db.Tenants
            .Where(t => t.Id == tenantKey)
            .Select(t => t.Name)
            .FirstOrDefaultAsync(ct);
        var workspace = string.IsNullOrWhiteSpace(tenantName) ? "a DCMS workspace" : tenantName!;
        var inviter = me.Name ?? me.Email;

        try
        {
            await emailQueue.EnqueueAsync(new EmailMessage(
                Recipients: [invitation.Email],
                Subject: $"You've been invited to {workspace} on DCMS",
                HtmlBody: InviteEmailBody(workspace, inviter, link, invitation.ExpiresAt),
                ReplyTo: me.Email,
                Purpose: "tenant-invitation",
                TenantId: invitation.TenantId,
                // The token is regenerated on every send, so it uniquely identifies
                // this delivery: a double-submitted invite dialog sends one email.
                DedupeKey: invitation.TokenHash), ct);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Invitations")
                .LogError(ex, "Failed to queue invitation email for {Email}.", invitation.Email);
        }
    }

    private static string InviteEmailBody(string workspace, string? inviter, string link, DateTimeOffset expiresAt)
    {
        var from = string.IsNullOrWhiteSpace(inviter) ? string.Empty : $" by {Enc(inviter!)}";
        return $"""
            <div style="font-family:system-ui,sans-serif;color:#0f172a;line-height:1.5">
              <h2 style="font-size:1.1rem">You've been invited to {Enc(workspace)}</h2>
              <p>You were invited{from} to join <strong>{Enc(workspace)}</strong> on DCMS. Click below to accept — you'll be asked to sign in or create an account first.</p>
              <p style="margin:1.5rem 0">
                <a href="{Enc(link)}" style="background:#0f172a;color:#fff;padding:.65rem 1.25rem;border-radius:.375rem;text-decoration:none;display:inline-block">Accept invitation</a>
              </p>
              <p style="font-size:.85rem;color:#475569">Or paste this link into your browser:<br /><a href="{Enc(link)}">{Enc(link)}</a></p>
              <p style="font-size:.85rem;color:#475569">This invitation expires on {expiresAt.UtcDateTime:D}. If you weren't expecting it, you can ignore this email.</p>
            </div>
            """;
    }

    private static string Enc(string value) => WebUtility.HtmlEncode(value);

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string HashToken(string token)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(hash);
    }

    private sealed record CreateInvitationRequest(string Email, string[]? RoleIds);
    private sealed record AcceptInvitationRequest(string Token);
}
