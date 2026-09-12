extern alias AdminApiApp;
extern alias IdentityApp;
extern alias PlatformApiApp;
using Dcms.Shared.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Security;

/// <summary>
/// The guard that keeps permission coverage from decaying.
///
/// <para><b>Why this exists at all.</b> The AI agent's loop runs in the browser (decision D1),
/// and each tool carries a <c>permission</c> field. That field is <i>user experience</i>: it
/// keeps the model from being offered a tool whose call could only ever end in a refusal. It is
/// not security, and it cannot be — anything the browser can send, a browser can send without
/// asking the tool registry first. The security boundary is the endpoint, every time.</para>
///
/// <para>So the thing actually worth asserting is not "the agent checks permissions" but
/// <b>"no endpoint the agent can reach is ungated"</b> — which is the same property the console
/// needs, and it protects both. Hence a coverage test over the whole endpoint table rather than
/// anything agent-shaped.</para>
///
/// <para>Every state-changing endpoint must either require a permission
/// (<c>.RequirePermission(...)</c>) or declare that something else gates it
/// (<c>.PermissionExempt("reason")</c>). Adding a mutating endpoint and thinking about neither
/// fails the build, which is the point: a rule everyone has to remember is a rule that rots.
/// Modelled on <see cref="Audit.AuditCoverageTestsBase{T}"/>, deliberately.</para>
///
/// <para>Container-free, like its audit sibling: it needs the endpoint table, not a database, so
/// it runs everywhere rather than only where Docker happens to be up.</para>
/// </summary>
public abstract class PermissionCoverageTestsBase<TEntryPoint> : IDisposable
    where TEntryPoint : class
{
    private readonly WebApplicationFactory<TEntryPoint> _factory =
        new WebApplicationFactory<TEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Tenancy:Migrate", "false");
            builder.UseSetting("Tenancy:ApplyRls", "false");
            builder.UseSetting("Identity:Migrate", "false");
            builder.UseSetting("Identity:Seed", "false");
            builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("ConnectionStrings:Redis", "localhost:1");
            builder.UseSetting("Nats:Url", "nats://localhost:1");
        });

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The declarations that count as "this endpoint's authority is decided".
    ///
    /// <para>Matched by attribute <b>name</b> rather than by type, because these live in
    /// <c>Dcms.AdminApi</c> and this base class is shared with three other hosts behind extern
    /// aliases. Names are stable here: each one is a deliberate, argued-for gate with a reason
    /// string attached, not something anybody adds casually.</para>
    /// </summary>
    private static readonly string[] GateAttributes =
    [
        // Another DCMS service, with a client-credentials token whose scope is checked.
        "AllowServicePrincipalAttribute",
        // Callable outside a tenant membership — self-scoped account and invitation routes.
        "AllowNonMemberTenantAttribute",
    ];

    /// <summary>
    /// Endpoints that predate this guard and have not yet declared how they are gated.
    ///
    /// <para><b>A ratchet, not a blessing.</b> Every entry here is "nobody has written down what
    /// gates this", which is not the same as "this is ungated" — most are gated by the tenant
    /// membership middleware, by ownership checked inside the handler, or by a signed payload.
    /// They are listed so the guard can start failing on <i>new</i> omissions today instead of
    /// waiting for a sweep of four services.</para>
    ///
    /// <para>The list may shrink and must never grow. Shrinking one entry means reading the
    /// endpoint, deciding what actually gates it, and saying so with <c>.RequirePermission(...)</c>
    /// or <c>.PermissionExempt("reason")</c>.</para>
    /// </summary>
    protected virtual string[] KnownUndeclared => [];

    [Fact]
    public void Every_mutating_endpoint_is_gated_by_a_permission_or_says_what_gates_it()
    {
        var ungated = MutatingEndpoints()
            .Where(e => !IsDecided(e))
            .Select(Describe)
            .Where(d => !KnownUndeclared.Contains(d, StringComparer.Ordinal))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        ungated.Should().BeEmpty(
            "every state-changing endpoint needs .RequirePermission(...) — or, when something else "
            + "gates it, one of the declarations that says so: a service-principal scope, a "
            + "self-scoped route, AllowAnonymous, or .PermissionExempt(\"reason\") for a gate the "
            + "endpoint table cannot see (ownership checked inside the handler, a signed payload). "
            + "The browser-side tool registry is UX, not security, so this is where the agent's "
            + "authority is actually bounded. Ungated:\n{0}",
            string.Join("\n", ungated));
    }

    private static bool IsDecided(RouteEndpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<PermissionMetadata>() is not null) return true;
        if (endpoint.Metadata.GetMetadata<PermissionExemptMetadata>() is not null) return true;
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null) return true;
        return endpoint.Metadata.Any(m => GateAttributes.Contains(m.GetType().Name, StringComparer.Ordinal));
    }

    [Fact]
    public void The_inherited_list_does_not_name_endpoints_that_no_longer_exist()
    {
        // A stale entry silently re-opens the hole it was named for: delete the endpoint, add a
        // new one at the same route, and the guard waves it through.
        var live = MutatingEndpoints().Select(Describe).ToHashSet(StringComparer.Ordinal);
        var stale = KnownUndeclared.Where(x => !live.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToList();

        stale.Should().BeEmpty(
            "these are listed as inherited but no longer exist; delete them from KnownUndeclared:\n{0}",
            string.Join("\n", stale));
    }

    [Fact]
    public void The_inherited_list_does_not_cover_an_endpoint_that_now_declares_itself()
    {
        // Keeps the ratchet honest: once an endpoint says how it is gated, its entry must go,
        // or the list stops measuring anything.
        var declared = MutatingEndpoints().Where(IsDecided).Select(Describe).ToHashSet(StringComparer.Ordinal);
        var redundant = KnownUndeclared.Where(declared.Contains).OrderBy(x => x, StringComparer.Ordinal).ToList();

        redundant.Should().BeEmpty(
            "these now declare how they are gated; remove them from KnownUndeclared:\n{0}",
            string.Join("\n", redundant));
    }

    [Fact]
    public void Exemptions_explain_themselves()
    {
        // A reason of "n/a" is silence wearing a declaration's clothes.
        var thin = MutatingEndpoints()
            .Select(e => (Reason: e.Metadata.GetMetadata<PermissionExemptMetadata>()?.Reason, Route: Describe(e)))
            .Where(x => x.Reason is not null && x.Reason.Trim().Length < 15)
            .Select(x => $"{x.Route}: \"{x.Reason}\"")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        thin.Should().BeEmpty(
            "an exemption is read by whoever revisits it; give it a real reason. Too thin:\n{0}",
            string.Join("\n", thin));
    }

    private IEnumerable<RouteEndpoint> MutatingEndpoints()
    {
        var source = _factory.Services.GetRequiredService<EndpointDataSource>();

        return source.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e =>
            {
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                // No declared methods means "any", which includes the mutating ones.
                return methods is null || methods.Any(IsMutating);
            });
    }

    private static bool IsMutating(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);

    private static string Describe(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        var verb = methods is null ? "ANY" : string.Join("/", methods.Where(IsMutating));
        return $"{verb} /{endpoint.RoutePattern.RawText?.TrimStart('/')}";
    }
}

/// <summary>
/// The control plane, and the one the AI agent reaches. Every tenant tool it holds —
/// publishing, deleting media, committing to a site's repository — lands here.
///
/// <para><b>Every endpoint the agent can reach already carries both a permission and an audit
/// action</b>; that was checked one by one when this guard was written. The inherited list
/// below is other surfaces.</para>
/// </summary>
public sealed class AdminApiPermissionCoverageTests : PermissionCoverageTestsBase<AdminApiApp::Program>
{
    protected override string[] KnownUndeclared =>
    [
        // Infrastructure probes, anonymous on purpose, but not marked as such.
        "ANY /health",
        "ANY /health/live",
        // SignalR. A hub's authority is declared per hub method, not on the mapped route.
        "ANY /api/hub/notifications",
        "ANY /api/hub/notifications/negotiate",
        "ANY /api/hub/sites",
        "ANY /api/hub/sites/negotiate",
        // Ownership, checked in the handler with `me.RequireUserId()` — a gate the endpoint
        // table cannot see, which is exactly what PermissionExempt exists to say out loud.
        "ANY /api/hub/notifications",
        "ANY /api/hub/notifications/negotiate",
        "POST /api/admin/notifications/read-all",
        "POST /api/admin/notifications/{id:guid}/dismiss",
        "POST /api/admin/notifications/{id:guid}/read",
        // Self-service: any authenticated person may create a workspace. Audited.
        "POST /api/admin/tenants",
        // The preview proxy. Tenant membership gates the request, but the site is resolved with
        // IgnoreQueryFilters(), so the tenant it proxies to comes from the siteId rather than
        // from the caller's own tenant. Worth a decision before it is written down as fine.
        "POST/PUT/PATCH/DELETE /api/admin/sites/{siteId:guid}/preview/api/{**path}",
    ];
}

/// <summary>
/// The delivery plane. Mostly anonymous by design — visitors submit forms, sign up and chat —
/// but that is a claim each route should make for itself rather than inherit from the host.
/// </summary>
public sealed class ContentApiPermissionCoverageTests : PermissionCoverageTestsBase<Program>
{
    protected override string[] KnownUndeclared =>
    [
        "ANY /health",
        "ANY /health/live",
        "ANY /hub/chat",
        "ANY /hub/chat/negotiate",
        "POST /api/collect",
        "POST /api/{slug}/collect",
        "POST /api/{slug}/forms/{formName}",
        "POST /api/{slug}/login",
        "POST /api/{slug}/refresh",
        "POST /api/{slug}/register",
    ];
}

/// <summary>
/// Account lifecycle. Gated by the auth handshake and by ownership rather than by tenant
/// permissions, which is why almost all of it is here rather than permission-gated.
/// </summary>
public sealed class IdentityPermissionCoverageTests : PermissionCoverageTestsBase<IdentityApp::Program>
{
    protected override string[] KnownUndeclared =>
    [
        "ANY /health",
        "ANY /health/live",
        "DELETE /account/api/me",
        "DELETE /account/api/ssh-keys/{id:long}",
        "DELETE /api/identity/users/{id:guid}/roles/{role}",
        "POST /account/api/password",
        "POST /account/api/ssh-keys",
        "POST /account/external/complete",
        "POST /account/forgot-password",
        "POST /account/login",
        "POST /account/register",
        "POST /account/reset-password",
        "POST /api/identity/users/{id:guid}/confirm-email",
        "POST /api/identity/users/{id:guid}/lock",
        "POST /api/identity/users/{id:guid}/roles",
        "POST /api/identity/users/{id:guid}/unlock",
        "POST /connect/authorize",
        "POST /connect/logout",
        "POST /connect/token",
        "POST /connect/userinfo",
    ];
}

/// <summary>
/// The platform console's API: suspending a tenant, handing out platform permissions, purging a
/// telemetry store. The smallest surface on the platform and the one where an ungated endpoint
/// would matter most — so this list is the most worth emptying.
/// </summary>
public sealed class PlatformApiPermissionCoverageTests : PermissionCoverageTestsBase<PlatformApiApp::Program>
{
    protected override string[] KnownUndeclared =>
    [
        "ANY /health",
        "ANY /health/live",
        "ANY /api/platform/hub/console",
        "ANY /api/platform/hub/console/negotiate",
        "DELETE /api/platform/certificates/{id:guid}",
        "DELETE /api/platform/purge/loki/{requestId}",
        "POST /api/platform/certificates/",
        "POST /api/platform/certificates/{id:guid}/reissue",
        "POST /api/platform/notifications/read-all",
        "POST /api/platform/notifications/{id:guid}/dismiss",
        "POST /api/platform/notifications/{id:guid}/read",
        "POST /api/platform/ops/analytics/prune",
        "POST /api/platform/purge/docker-logs",
        "POST /api/platform/purge/loki",
        "POST /api/platform/purge/prometheus",
        "POST /api/platform/tenants/{id:guid}/resume",
        "POST /api/platform/tenants/{id:guid}/suspend",
        "PUT /api/platform/certificates/{id:guid}",
        "PUT /api/platform/roles/{roleName}/permissions",
    ];
}
