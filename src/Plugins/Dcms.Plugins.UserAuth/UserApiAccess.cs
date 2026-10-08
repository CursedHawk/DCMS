using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.UserAuth;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.UserAuth;

/// <summary>
/// Who may call a plugin instance's site API (ADR 0022): <c>/api/{slug}/…</c> — the instance's own
/// routes, its content delivery and its site contracts — for an instance the tenant restricted
/// (<see cref="ApiRule"/>). This, not the edge's path rules, is what protects a single-page app:
/// its route changes never reach the edge, but its data always comes through here.
/// </summary>
/// <remarks>
/// A request gate (<see cref="IPluginRequestGate"/>) on the site plane: content-api runs it after
/// authentication, with the endpoint routed. A tenant with no rules costs one cached lookup.
/// Also leaves on the request which restricted instances the caller may read
/// (<see cref="ApiRuleVisibility.ReadableItem"/>), for search and tags, which read across instances.
/// </remarks>
public sealed class UserApiAccess : IPluginRequestGate
{
    public const string SignInPath = "/.edge/site/signin";

    /// <summary>Every route of one instance: its own (PluginEndpoints), delivery, config and site contracts.</summary>
    private const string InstancePrefix = "/api/{slug}";

    // ponytail: per process, so a change made in the console reaches content-api within this.
    private static readonly TimeSpan RulesTtl = TimeSpan.FromSeconds(10);

    /// <summary><c>{plugin}:{slug}:api:read</c> for reads, <c>…:api:write</c> for everything else.</summary>
    public static string Permission(string pluginId, string slug, bool write) => $"{pluginId}:{slug}:api:{(write ? "write" : "read")}";

    public async Task InvokeAsync(HttpContext http, RequestDelegate next)
    {
        if (http.RequestServices.GetRequiredService<ITenantContext>().TenantId is not { } tenantId)
        {
            await next(http);
            return;
        }
        if (await RulesAsync(http, tenantId) is not { Count: > 0 } rules)
        {
            await next(http);
            return;
        }

        // On every request of such a tenant, not only /api/{slug}: search and tags read it too.
        var enabled = await http.RequestServices.GetRequiredService<PluginContextFactory>().EnabledInstancesAsync(tenantId, http.RequestAborted);
        var permissions = await PermissionsAsync(http, tenantId);
        http.Items[ApiRuleVisibility.ReadableItem] = enabled
            .Where(i => rules.TryGetValue(i.InstanceId, out var access) && Allows(access, permissions, i, write: false))
            .Select(i => i.InstanceId)
            .ToHashSet();

        // What routing matched, not a reading of the path: "/API/news", "/api/news/" and
        // "/api/{instanceId}" (the contract dispatcher takes either) all reach the same endpoint.
        if (http.GetEndpoint() is not RouteEndpoint { RoutePattern.RawText: { } pattern }
            || !pattern.StartsWith(InstancePrefix, StringComparison.OrdinalIgnoreCase)
            || http.GetRouteValue("slug") is not string slug
            || enabled.FirstOrDefault(i => string.Equals(i.Slug, slug, StringComparison.Ordinal)
                                           || string.Equals(i.InstanceId.ToString(), slug, StringComparison.OrdinalIgnoreCase)) is not { } instance
            || !rules.TryGetValue(instance.InstanceId, out var rule))
        {
            await next(http);
            return;
        }
        if (permissions is null)
        {
            await RefuseAsync(http, StatusCodes.Status401Unauthorized, "signin_required", "Sign in to use this.");
            return;
        }
        if (!Allows(rule, permissions, instance, IsWrite(http)))
        {
            await RefuseAsync(http, StatusCodes.Status403Forbidden, "forbidden", "You don't have access to this.");
            return;
        }
        // Whatever the endpoint says: this answer is this user's, not the next caller's.
        http.Response.OnStarting(() =>
        {
            http.Response.Headers.CacheControl = "private, no-store";
            return Task.CompletedTask;
        });
        await next(http);
    }

    private static bool Allows(ApiAccess access, IReadOnlySet<string>? permissions, PluginInstanceContext instance, bool write) =>
        permissions is not null
        && (access == ApiAccess.SignedIn || permissions.Contains(Permission(instance.PluginId, instance.Slug, write)));

    /// <summary>
    /// A contract call is always a POST, so it is judged by its operation instead: a read is a
    /// read. An operation nobody provides is a write — the dispatcher answers 404 for it anyway.
    /// </summary>
    private static bool IsWrite(HttpContext http)
    {
        if (http.GetRouteValue("contractId") is string contractId && http.GetRouteValue("operation") is string operation)
        {
            return http.RequestServices.GetRequiredService<PluginRegistry>().FindContract(contractId)?.Descriptor
                .FindOperation(operation)?.Risk != OpRisk.Read;
        }
        var method = http.Request.Method;
        return !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));
    }

    /// <summary>The caller's permissions; null when nobody is signed in.</summary>
    private static async Task<IReadOnlySet<string>?> PermissionsAsync(HttpContext http, Guid tenantId)
    {
        if (await http.CurrentUserAsync() is not { } user)
        {
            return null;
        }
        if (http.Items[UserAccess.PermissionsItem] is HashSet<string> known)
        {
            return known;
        }
        var permissions = await UserAccess.ForAsync(http.RequestServices.GetRequiredService<UserAuthDbContext>(),
            tenantId, user.Id, user.Groups, http.RequestAborted);
        http.Items[UserAccess.PermissionsItem] = permissions;
        return permissions;
    }

    private static async Task<IReadOnlyDictionary<Guid, ApiAccess>> RulesAsync(HttpContext http, Guid tenantId)
    {
        var cache = http.RequestServices.GetRequiredService<IMemoryCache>();
        return (await cache.GetOrCreateAsync($"userauth:api-rules:{tenantId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = RulesTtl;
            var db = http.RequestServices.GetRequiredService<UserAuthDbContext>();
            using var rls = RlsScope.Tenant(tenantId);
            return (IReadOnlyDictionary<Guid, ApiAccess>)await db.ApiRules.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.TenantId == tenantId)
                .ToDictionaryAsync(r => r.InstanceId, r => r.Access, http.RequestAborted);
        }))!;
    }

    private static Task RefuseAsync(HttpContext http, int status, string error, string title)
    {
        http.Response.StatusCode = status;
        http.Response.Headers.CacheControl = "no-store";
        return http.Response.WriteAsJsonAsync(new { type = "about:blank", title, status, error, signInUrl = SignInPath },
            options: null, contentType: "application/problem+json");
    }
}
