using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.ApiClientGen;

/// <summary>What the current tenant's content API looks like, as every generator here needs it.</summary>
/// <param name="InputKey">
/// A hash of the inputs — instances, servers, whether tags are published. Changes whenever the
/// document could; a cache key, not a statement that it did.
/// </param>
public sealed record TenantApiSnapshot(
    JsonObject Document,
    IReadOnlyList<GeneratedInstance> Instances,
    IReadOnlyList<string> Servers,
    string TenantSlug,
    string InputKey)
{
    public string Json { get; } = Document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>
/// Resolves the current tenant's content-API OpenAPI document — once, for everything in admin-api
/// that needs it.
///
/// <para>The API Docs preview, the client download, the starter download and the IDE's starter
/// and Refresh API each used to resolve it themselves, from copies of the same twenty lines. The
/// copies had already drifted: none of them passed <c>tagging</c>, so the admin's documents and
/// every generated client lacked the <c>/api/tags</c> index that content-api actually serves to
/// any tenant with a published tag.</para>
/// </summary>
public sealed class TenantApiResolver(
    ITenantContext tenant,
    CmsDbContext db,
    TenancyDbContext tenancy,
    OpenApiAssembler assembler)
{
    public async Task<TenantApiSnapshot?> ResolveAsync(CancellationToken ct)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return null;
        }

        var instances = await db.PluginInstances.AsNoTracking()
            .Where(p => p.Enabled)
            .OrderBy(p => p.Slug)
            .ToListAsync(ct);

        // The tenant's verified, site-linked domains, primary first, so a docs "try it" call
        // reaches a real host. TenancyDbContext is tenant-scoped, so this is this tenant's only.
        var servers = (await tenancy.Domains.AsNoTracking()
                .Where(d => d.VerifiedAt != null && d.SiteId != null)
                .OrderByDescending(d => d.IsPrimary)
                .ThenBy(d => d.Hostname)
                .Select(d => d.Hostname)
                .ToListAsync(ct))
            .Select(h => $"https://{h}")
            .ToList();

        // Same rule as content-api's own document, or the two disagree about what exists.
        var tagging = await TagQueries.AnyAsync(db, tenantId, publishedOnly: true, ct);

        var contexts = instances.Select(p => new PluginInstanceContext(
            p.Id, p.TenantId, p.PluginId, p.Slug, p.Name, p.Description,
            JsonDocument.Parse(string.IsNullOrWhiteSpace(p.ConfigJson) ? "{}" : p.ConfigJson))).ToList();

        var tenantSlug = tenant.TenantSlug ?? tenantId.ToString();
        var document = assembler.Build(tenantSlug, contexts, servers, tagging);

        var inputKey = PluginInstanceFingerprint.Hash(
            $"{PluginInstanceFingerprint.Of(instances)}|servers:{string.Join(',', servers)}|tags:{tagging}|tenant:{tenantSlug}");

        return new TenantApiSnapshot(
            document,
            instances.Select(p => new GeneratedInstance(p.Slug, p.PluginId, p.Name, p.Description)).ToList(),
            servers,
            tenantSlug,
            inputKey);
    }
}
