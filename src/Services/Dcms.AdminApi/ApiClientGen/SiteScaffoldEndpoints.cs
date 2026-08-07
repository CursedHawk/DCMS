using System.IO.Compression;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Runtime;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;

namespace Dcms.AdminApi.ApiClientGen;

/// <summary>
/// Starter scaffolds for a brand-new Mode B (ReactApp) site, generated from the
/// current tenant's content-API OpenAPI — the same source as the API Docs tab
/// downloads (<see cref="ApiClientEndpoints"/>). The IDE offers these when a new
/// React app is created and seeds the returned <c>{ files }</c> map into the
/// working draft. The scaffold includes its own package.json + lockfile: a Mode B
/// site is a complete, self-contained project that the builder installs and builds.
/// </summary>
public static class SiteScaffoldEndpoints
{
    public static IEndpointRouteBuilder MapSiteScaffoldEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/sites/{siteId}/starter-files", async (
            string flavor, ITenantContext tenant, CmsDbContext db, TenancyDbContext tenancy,
            OpenApiAssembler assembler, CancellationToken ct) =>
        {
            var resolved = await ApiClientEndpoints.ResolveAsync(tenant, db, tenancy, assembler, ct);
            if (resolved is not { } r)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }

            var openApiJson = r.Doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var files = new Dictionary<string, string>(StringComparer.Ordinal);

            switch (flavor?.ToLowerInvariant())
            {
                case "openapi":
                    files["openapi.json"] = openApiJson;
                    break;

                case "client":
                    Merge(files, Unzip(ClientPackageBuilder.Build(r.Doc, r.Instances)));
                    files["openapi.json"] = openApiJson;
                    break;

                case "starter":
                    Merge(files, Unzip(SiteStarterEmitter.Build(r.Doc, r.Instances, r.Servers.FirstOrDefault())));
                    files["openapi.json"] = openApiJson;
                    break;

                default:
                    return Results.BadRequest(new { error = "flavor must be one of: openapi, client, starter." });
            }

            return Results.Ok(new { files });
        }).RequirePermission(PlatformPermissions.SiteEdit);

        return app;
    }

    /// <summary>Read a builder's zip into a flat path→content map (including package.json
    /// + lockfile, so the seeded site is a complete, buildable project).</summary>
    private static Dictionary<string, string> Unzip(byte[] zip)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var ms = new MemoryStream(zip);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
            using var reader = new StreamReader(entry.Open());
            map[entry.FullName] = reader.ReadToEnd();
        }
        return map;
    }

    private static void Merge(Dictionary<string, string> into, Dictionary<string, string> from)
    {
        foreach (var (k, v) in from) into[k] = v;
    }
}
