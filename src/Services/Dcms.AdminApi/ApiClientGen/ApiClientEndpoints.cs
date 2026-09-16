namespace Dcms.AdminApi.ApiClientGen;

/// <summary>
/// Downloads for the current tenant, generated from its content-API OpenAPI document: a typed
/// TypeScript client, and a runnable Vite + React site on the content template. Both resolve the
/// tenant through <see cref="TenantApiResolver"/>, as the IDE's templates and Refresh API do, so a
/// download and a site started in the editor are built from the same document.
/// </summary>
public static class ApiClientEndpoints
{
    public static IEndpointRouteBuilder MapApiClientDownload(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/api-client.zip", async (TenantApiResolver resolver, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(ct) is not { } api)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }
            var zip = ClientPackageBuilder.Build(api.Document, api.Instances);
            return Results.File(zip, "application/zip", $"{Slugify(api.TenantSlug)}-api-client.zip");
        }).RequireAuthorization();

        app.MapGet("/api/admin/site-starter.zip", async (TenantApiResolver resolver, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(ct) is not { } api)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }
            var zip = SiteTemplates.BuildDownload(api, GeneratedLayer.Build(api));
            return Results.File(zip, "application/zip", $"{Slugify(api.TenantSlug)}-site-starter.zip");
        }).RequireAuthorization();

        return app;
    }

    private static string Slugify(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
        var slug = new string(chars).Trim('-');
        return string.IsNullOrEmpty(slug) ? "tenant" : slug;
    }
}
