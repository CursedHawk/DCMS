using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.ApiClientGen;

/// <summary>
/// Downloads for the current tenant, generated from its content-API OpenAPI doc:
/// a fully-typed TypeScript client, and a ready-to-run Vite + React starter site that
/// embeds it. Both resolve the tenant and its enabled plugin instances exactly like
/// <c>OpenApiPreviewEndpoints</c> and require the admin bearer token.
/// </summary>
public static class ApiClientEndpoints
{
    public static IEndpointRouteBuilder MapApiClientDownload(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/api-client.zip", async (
            ITenantContext tenant, CmsDbContext db, TenancyDbContext tenancy,
            OpenApiAssembler assembler, CancellationToken ct) =>
        {
            var resolved = await ResolveAsync(tenant, db, tenancy, assembler, ct);
            if (resolved is not { } r)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }
            var zip = ClientPackageBuilder.Build(r.Doc, r.Instances);
            return Results.File(zip, "application/zip", $"{Slugify(r.TenantSlug)}-api-client.zip");
        }).RequireAuthorization();

        app.MapGet("/api/admin/site-starter.zip", async (
            ITenantContext tenant, CmsDbContext db, TenancyDbContext tenancy,
            OpenApiAssembler assembler, CancellationToken ct) =>
        {
            var resolved = await ResolveAsync(tenant, db, tenancy, assembler, ct);
            if (resolved is not { } r)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }
            var zip = SiteStarterEmitter.Build(r.Doc, r.Instances, r.Servers.FirstOrDefault());
            return Results.File(zip, "application/zip", $"{Slugify(r.TenantSlug)}-site-starter.zip");
        }).RequireAuthorization();

        return app;
    }

    private sealed record Resolved(
        JsonObject Doc, List<GeneratedInstance> Instances, List<string> Servers, string TenantSlug);

    private static async Task<Resolved?> ResolveAsync(
        ITenantContext tenant, CmsDbContext db, TenancyDbContext tenancy,
        OpenApiAssembler assembler, CancellationToken ct)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return null;
        }

        var instances = await db.PluginInstances.AsNoTracking()
            .Where(p => p.Enabled)
            .OrderBy(p => p.Slug)
            .ToListAsync(ct);

        // Advertise the tenant's verified, site-linked domains as OpenAPI servers,
        // primary first — mirrors the preview endpoint.
        var servers = (await tenancy.Domains.AsNoTracking()
                .Where(d => d.VerifiedAt != null && d.SiteId != null)
                .OrderByDescending(d => d.IsPrimary)
                .ThenBy(d => d.Hostname)
                .Select(d => d.Hostname)
                .ToListAsync(ct))
            .Select(h => $"https://{h}")
            .ToList();

        var contexts = instances.Select(p => new PluginInstanceContext(
            p.Id, p.TenantId, p.PluginId, p.Slug, p.Name, p.Description,
            JsonDocument.Parse(string.IsNullOrWhiteSpace(p.ConfigJson) ? "{}" : p.ConfigJson))).ToList();

        var tenantSlug = tenant.TenantSlug ?? tenantId.ToString();
        var doc = assembler.Build(tenantSlug, contexts, servers);
        var genInstances = instances
            .Select(p => new GeneratedInstance(p.Slug, p.PluginId, p.Name))
            .ToList();

        return new Resolved(doc, genInstances, servers, tenantSlug);
    }

    private static string Slugify(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
        var slug = new string(chars).Trim('-');
        return string.IsNullOrEmpty(slug) ? "tenant" : slug;
    }
}
