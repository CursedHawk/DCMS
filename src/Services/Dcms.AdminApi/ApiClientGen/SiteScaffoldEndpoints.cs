using Dcms.Shared.Caching;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;

namespace Dcms.AdminApi.ApiClientGen;

/// <summary>
/// What the IDE writes into a Mode B site: a whole starter template for a new one, and the
/// DCMS-owned <see cref="GeneratedLayer"/> for an existing one.
///
/// <para>All three read the same <see cref="TenantApiResolver"/> snapshot and the same layer
/// builder, so starting a site, pressing Refresh API and the editor's automatic refresh cannot
/// disagree about what the generated files are.</para>
/// </summary>
public static class SiteScaffoldEndpoints
{
    /// <summary>
    /// How long a computed fingerprint is trusted. Its key already changes with every input, so
    /// this bounds only the window in which a generator change deployed under an unchanged key is
    /// not yet noticed — and the key carries the build's module id, so even that is closed.
    /// </summary>
    private static readonly TimeSpan FingerprintTtl = TimeSpan.FromHours(24);

    /// <summary>The build, so a deploy that changes the generator invalidates every cached fingerprint.</summary>
    private static readonly string BuildId = typeof(GeneratedLayer).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];

    public static IEndpointRouteBuilder MapSiteScaffoldEndpoints(this IEndpointRouteBuilder app)
    {
        // A complete new site from one of the starter templates.
        app.MapGet("/api/admin/sites/{siteId}/starter-files", async (
            string? template, TenantApiResolver resolver, CancellationToken ct) =>
        {
            if (!SiteTemplates.Exists(template))
            {
                return Results.BadRequest(new { error = $"template must be one of: {string.Join(", ", SiteTemplates.Ids)}." });
            }
            if (await resolver.ResolveAsync(ct) is not { } api)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }
            var generated = GeneratedLayer.Build(api);
            return Results.Ok(new { fingerprint = generated.Fingerprint, files = SiteTemplates.Materialize(template!, generated) });
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Only the DCMS-owned files, for Refresh API and the editor's automatic refresh. Never a
        // file the author wrote: the layer is src/api/, src/dcms/ and openapi.json, nothing else.
        app.MapGet("/api/admin/sites/{siteId}/generated-files", async (
            TenantApiResolver resolver, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(ct) is not { } api)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }
            var generated = GeneratedLayer.Build(api);
            return Results.Ok(new { fingerprint = generated.Fingerprint, files = generated.Files });
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // The cheap question the editor asks on open and whenever the tenant's plugins change:
        // "would regenerating write anything?" Compared with the site's manifest, it is the whole
        // decision, so the layer itself is only fetched when the answer is yes.
        app.MapGet("/api/admin/api-fingerprint", async (
            ITenantContext tenant, TenantApiResolver resolver, ICacheService cache, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(ct) is not { } api)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }
            var key = $"t:{tenant.TenantId}:api-fingerprint:{api.InputKey}:{BuildId}";
            var fingerprint = await cache.GetAsync<string>(key, ct);
            if (fingerprint is null)
            {
                fingerprint = GeneratedLayer.Build(api).Fingerprint;
                await cache.SetAsync(key, fingerprint, FingerprintTtl, ct);
            }
            return Results.Ok(new { fingerprint });
        }).RequirePermission(PlatformPermissions.SiteEdit);

        return app;
    }
}
