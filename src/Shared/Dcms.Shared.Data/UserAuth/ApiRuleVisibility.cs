using Dcms.Shared.Data.Rls;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.UserAuth;

/// <summary>
/// What a caller may not see of a tenant's API-restricted plugin instances (<see cref="ApiRule"/>),
/// for code that reads across instances — search, tags — rather than through one instance's
/// <c>/api/{slug}</c>, which content-api's API access check guards directly.
/// </summary>
public static class ApiRuleVisibility
{
    /// <summary>
    /// The restricted instances this request's caller may read, put on the request by content-api's
    /// API access check (the user-auth plugin). Absent: none.
    /// </summary>
    public const string ReadableItem = "dcms.userauth.readable-instances";

    /// <summary>
    /// The tenant's restricted instances the caller cannot read, on the public site plane
    /// (<paramref name="sitePlane"/>): all of them except those content-api found this request may
    /// read. Fail-closed: a site call content-api did not check — the chat hub, the chatbot in the
    /// background — reads none of them. Off the site plane (the console) nothing is hidden.
    /// </summary>
    public static async Task<HashSet<Guid>> HiddenAsync(UserAuthDbContext db, Guid tenantId, HttpContext? http, bool sitePlane, CancellationToken ct)
    {
        if (!sitePlane)
        {
            return [];
        }
        using var rls = RlsScope.Tenant(tenantId);
        var restricted = await db.ApiRules.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.TenantId == tenantId).Select(r => r.InstanceId).ToListAsync(ct);
        var hidden = restricted.ToHashSet();
        if (http?.Items[ReadableItem] is IReadOnlySet<Guid> readable)
        {
            hidden.ExceptWith(readable);
        }
        return hidden;
    }
}
