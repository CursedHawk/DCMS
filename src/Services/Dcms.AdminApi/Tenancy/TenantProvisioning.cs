using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// Creates a tenant with its system roles and an owner membership, then emits
/// the tenant.created and membership.changed events. All rows are stamped with
/// the new tenant id explicitly since there is no ambient tenant yet.
/// </summary>
public sealed class TenantProvisioning(TenancyDbContext db, IEventPublisher events)
{
    public const string OwnerRole = "Owner";
    public const string MemberRoleName = "Member";

    public async Task<Tenant> CreateTenantAsync(
        string slug, string? name, Guid ownerUserId, string ownerEmail, CancellationToken ct)
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid().ToString(),
            Identifier = slug,
            Name = name ?? slug,
            Status = TenantStatus.Active,
        };
        var tenantId = tenant.TenantId;
        db.Tenants.Add(tenant);

        // Owner: every platform permission. Member: a read-only starter set.
        var ownerRole = NewRole(tenantId, OwnerRole, PlatformPermissions.All);
        var memberRole = NewRole(tenantId, MemberRoleName,
            [PlatformPermissions.MediaRead, PlatformPermissions.AnalyticsRead]);
        db.TenantRoles.AddRange(ownerRole, memberRole);

        var membership = new TenantMembership
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = ownerUserId,
            Email = ownerEmail,
        };
        membership.Roles.Add(new MemberRole
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            MembershipId = membership.Id,
            TenantRoleId = ownerRole.Id,
        });
        db.Memberships.Add(membership);

        await db.SaveChangesAsync(ct);

        await events.PublishAsync(Subjects.TenantCreated,
            new TenantCreated(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, slug, tenant.Name!), ct);
        await events.PublishAsync(Subjects.MembershipChanged,
            new MembershipChanged(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, ownerUserId), ct);

        return tenant;
    }

    private static TenantRole NewRole(Guid tenantId, string name, IReadOnlyList<string> permissions)
    {
        var role = new TenantRole
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            IsSystem = true,
        };
        foreach (var permission in permissions)
        {
            role.Permissions.Add(new TenantRolePermission
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                TenantRoleId = role.Id,
                Permission = permission,
            });
        }
        return role;
    }
}
