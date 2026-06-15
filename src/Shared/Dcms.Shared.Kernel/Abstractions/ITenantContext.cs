namespace Dcms.Shared.Kernel.Abstractions;

/// <summary>
/// Ambient tenant for the current request or message. Populated by Finbuckle middleware
/// on HTTP paths and by message envelope metadata on NATS consumers.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    string? TenantSlug { get; }
    bool HasTenant => TenantId is not null;
}
