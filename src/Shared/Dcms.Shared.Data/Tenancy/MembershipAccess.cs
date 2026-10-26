namespace Dcms.Shared.Data.Tenancy;

public static class MembershipAccess
{
    /// <summary>
    /// The memberships that open their tenant. Any membership does, except in a workspace with
    /// environments (ADR 0026): its members are mirrored into every environment and a role there
    /// is what lets them in, so a role-less one belongs to the workspace and not to this
    /// environment. Every gate that asks "is this caller a member" asks through this.
    /// </summary>
    public static IQueryable<TenantMembership> ThatOpen(this IQueryable<TenantMembership> memberships, bool hasEnvironments) =>
        hasEnvironments ? memberships.Where(m => m.Roles.Any()) : memberships;
}
