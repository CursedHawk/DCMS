using Dcms.PluginSdk.Abstractions;
using System.Security.Claims;
using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Dcms.Shared.Security.Agents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dcms.PluginSdk.Runtime.Contracts;

/// <summary>
/// The HTTP face of <see cref="ContractDispatcher"/>. Every route is a thin adapter: the plane,
/// the tenant, the actor and a permission check go in; the dispatcher decides everything else.
/// </summary>
public static class ContractEndpoints
{
    /// <summary>
    /// Site plane (content-api). An operation lives under its provider instance —
    /// <c>POST /api/{slug}/_contracts/{contractId}/{operation}</c> — so the slug picks the
    /// provider, and the per-tenant OpenAPI document and generated client describe it beside
    /// the instance's other routes. <c>GET /api/_contracts</c> lists what the site may call.
    /// </summary>
    public static IEndpointRouteBuilder MapDcmsContractSiteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/_contracts", async (ITenantContext tenant, ContractDispatcher dispatcher, CancellationToken ct) =>
            tenant.TenantId is { } tenantId
                ? Results.Ok(await dispatcher.CatalogAsync(tenantId, ContractPlane.Site, NoPermissions, ct))
                : Results.NotFound());

        app.MapPost("/api/{slug}/_contracts/{contractId}/{operation}", async (
            string slug, string contractId, string operation, HttpRequest request,
            ITenantContext tenant, ContractDispatcher dispatcher, PluginContextFactory factory, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.NotFound();
            }
            // Site callers are anonymous to the platform; an operation that needs to know the
            // visitor (visitors.identity@1) reads their token from the request itself.
            return await dispatcher.InvokeAsync(
                tenantId, contractId, operation, slug, await ReadInputAsync(request, ct),
                ContractPlane.Site, factory.CurrentActor(), NoPermissions, ct);
        })
        // Every operation behind this route declares its own gate, and the site plane refuses
        // any that names a permission (a site caller holds none).
        .PermissionExempt("Dispatcher: only Site-exposed operations; any operation with a Permission is refused on this plane.")
        .AuditExempt("Per operation, not per route: the contract proxy records every Safe or Dangerous call as plugin.contract.invoked; reads are not audited.");

        return app;
    }

    /// <summary>
    /// Admin plane (admin-api), for members and for AI agents acting for them:
    /// <c>GET /api/admin/contracts?plane=admin|ai</c> and
    /// <c>POST /api/admin/contracts/{contractId}/{operation}?instance=&amp;plane=</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapDcmsContractAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/contracts", async (
            string? plane, ClaimsPrincipal user, IAuthorizationService authz,
            ITenantContext tenant, ContractDispatcher dispatcher, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId || ParsePlane(plane) is not { } p)
            {
                return Results.BadRequest(new { error = "A tenant and plane=admin|ai are required." });
            }
            if (AgentRefusal(user, p) is { } refused) return refused;
            var catalog = await dispatcher.CatalogAsync(tenantId, p, Permissions(user, authz), ct);
            // A read-only agent is shown only what it may call.
            return Results.Ok(user.IsReadOnlyAgent()
                ? catalog.Select(c => c with { Operations = [.. c.Operations.Where(o => o.Risk == OpRisk.Read)] }).Where(c => c.Operations.Count > 0).ToList()
                : catalog);
        }).RequireAuthorization()
          .PermissionExempt("Lists only operations the caller's own permissions allow.")
          .Cli("contracts.catalog", "Lists the plugin and platform operations you may call (plane=ai for agents).", CliRisk.Read);

        app.MapPost("/api/admin/contracts/{contractId}/{operation}", async (
            string contractId, string operation, string? instance, string? plane, HttpRequest request,
            ClaimsPrincipal user, IAuthorizationService authz, ITenantContext tenant,
            ContractDispatcher dispatcher, PluginContextFactory factory, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId || ParsePlane(plane ?? "admin") is not { } p)
            {
                return Results.BadRequest(new { error = "A tenant and plane=admin|ai are required." });
            }
            if (AgentRefusal(user, p) is { } refused) return refused;
            if (user.IsReadOnlyAgent() && dispatcher.RiskOf(contractId, operation) is not OpRisk.Read)
            {
                return Results.Json(new { error = "This agent token may only read.", code = "agent_scope" }, statusCode: StatusCodes.Status403Forbidden);
            }
            return await dispatcher.InvokeAsync(
                tenantId, contractId, operation, instance, await ReadInputAsync(request, ct),
                p, factory.CurrentActor(), Permissions(user, authz), ct);
        }).RequireAuthorization()
          .PermissionExempt("Dispatcher: checks each operation's own Permission for the caller before invoking it.")
          .Cli("contracts.invoke", "Calls one plugin or platform operation (plane=ai for agents).", CliRisk.PerOperation)
          .AuditExempt("Per operation, not per route: the contract proxy records every Safe or Dangerous call as plugin.contract.invoked; reads are not audited.");

        return app;
    }

    /// <summary>
    /// A desktop agent (ADR 0032) uses the AI plane only, as the console's own assistant does: what
    /// each operation exposes to AI, on the instances whose AI tools are on.
    /// </summary>
    private static IResult? AgentRefusal(ClaimsPrincipal user, ContractPlane plane) =>
        user.IsAgent() && plane != ContractPlane.Ai
            ? Results.Json(new { error = "Agents call operations on plane=ai.", code = "agent_scope" }, statusCode: StatusCodes.Status403Forbidden)
            : null;

    private static ContractPlane? ParsePlane(string? plane) => plane?.ToLowerInvariant() switch
    {
        "admin" => ContractPlane.Admin,
        "ai" => ContractPlane.Ai,
        _ => null,
    };

    private static Task<bool> NoPermissions(string permission) => Task.FromResult(false);

    /// <summary>
    /// The same permission check <c>RequirePermission</c> uses, including the SuperAdmin bypass. The
    /// dispatcher asks about the providing instance; a plugin-wide key it still asks about (another
    /// plugin's, on an open contract) is held on any instance.
    /// </summary>
    private static Func<string, Task<bool>> Permissions(ClaimsPrincipal user, IAuthorizationService authz) =>
        async permission => (await authz.AuthorizeAsync(user, null,
            PluginPermissions.TryParse(permission, out _, out var slug, out _) && slug is null
                ? PermissionPolicyProvider.AnyInstancePolicyName(permission)
                : PermissionPolicyProvider.PolicyName(permission))).Succeeded;

    private static async Task<JsonElement?> ReadInputAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is 0 || !request.HasJsonContentType())
        {
            return null;
        }
        try
        {
            using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Malformed JSON reaches the dispatcher as "no input"; a required input then fails
            // binding with a 400 naming what is missing.
            return null;
        }
    }
}
