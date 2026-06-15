using System.Security.Cryptography;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy;

public static class InvitationEndpoints
{
    public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        // Create an invitation (tenant-scoped). The raw token is returned once
        // (dev) and would be emailed via Mailpit in a fuller implementation.
        app.MapPost("/api/admin/invitations", async (
            CreateInvitationRequest body, TenancyDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Email))
            {
                return Results.BadRequest(new { error = "Email required." });
            }

            var token = GenerateToken();
            var invitation = new Invitation
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId!.Value,
                Email = body.Email.Trim().ToLowerInvariant(),
                TokenHash = HashToken(token),
                RoleIdsCsv = string.Join(',', body.RoleIds ?? []),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            };
            db.Invitations.Add(invitation);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/admin/invitations/{invitation.Id}", new
            {
                invitationId = invitation.Id,
                email = invitation.Email,
                token,
                expiresAt = invitation.ExpiresAt,
            });
        }).RequirePermission(PlatformPermissions.MembersManage);

        // Accept an invitation. Authenticated; the caller becomes a member of the
        // invited tenant with the invitation's roles. Operates across tenants, so
        // query filters are bypassed and tenant ids are set explicitly.
        app.MapPost("/api/admin/invitations/accept", async (
            AcceptInvitationRequest body, CurrentUser me, TenancyDbContext db,
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

            var roleIds = invitation.RoleIdsCsv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToList();

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

            return Results.Ok(new { tenantId, membershipId = membership.Id });
        }).RequireAuthorization();

        return app;
    }

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
