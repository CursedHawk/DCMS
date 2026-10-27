using Dcms.Shared.Data.Rls;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

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
        var hidden = (http?.RequestServices.GetService<IMemoryCache>() is { } cache
            ? await RulesAsync(db, cache, tenantId, ct)
            : await LoadAsync(db, tenantId, ct)).Keys.ToHashSet();
        if (http?.Items[ReadableItem] is IReadOnlySet<Guid> readable)
        {
            hidden.ExceptWith(readable);
        }
        return hidden;
    }

    // ponytail: per process, so a change made in the console reaches content-api within this.
    private static readonly TimeSpan RulesTtl = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The tenant's API rules by instance, held for <see cref="RulesTtl"/>. One entry for both readers:
    /// content-api's API access check on every request, and the cross-instance reads above (which
    /// queried on every request of their own until 2026-10-23).
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, ApiAccess>> RulesAsync(
        UserAuthDbContext db, IMemoryCache cache, Guid tenantId, CancellationToken ct) =>
        (await cache.GetOrCreateAsync($"userauth:api-rules:{tenantId}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = RulesTtl;
            return LoadAsync(db, tenantId, ct);
        }))!;

    private static async Task<IReadOnlyDictionary<Guid, ApiAccess>> LoadAsync(UserAuthDbContext db, Guid tenantId, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(tenantId);
        return await db.ApiRules.IgnoreTenantFilter().AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .ToDictionaryAsync(r => r.InstanceId, r => r.Access, ct);
    }
}
