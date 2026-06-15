namespace Dcms.Shared.Data;

/// <summary>
/// Base for every tenant-scoped entity. All composite indexes must lead with
/// TenantId; Finbuckle query filters (wired per DbContext in Phase 3) and
/// Postgres RLS (Phase 12) both key off this column.
/// </summary>
public abstract class TenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
}
