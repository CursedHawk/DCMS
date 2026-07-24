using Dcms.Shared.Kernel.Abstractions;

namespace Dcms.ContentApi;

/// <summary>
/// Delivery-plane sandbox context: reads the <c>X-Dcms-Sandbox</c> request
/// header set by the admin-api preview proxy. When present ("1"/"true"),
/// tenant-scoped writes are routed to the per-tenant sandbox space so a site
/// preview's test data never touches the tenant's live records.
/// </summary>
public sealed class HeaderSandboxContext(IHttpContextAccessor accessor) : ISandboxContext
{
    public const string Header = "X-Dcms-Sandbox";

    public bool IsSandbox
        => accessor.HttpContext?.Request.Headers[Header].ToString() is "1" or "true";
}
