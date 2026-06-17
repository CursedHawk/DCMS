using Dcms.Shared.Storage;
using Microsoft.Extensions.Options;

namespace Dcms.SiteHost;

/// <summary>
/// Serves a tenant site's static build artifacts from MinIO, resolving the
/// active build from the request Host. Unknown paths fall back to index.html
/// (SPA-style). /api and /hub are handled separately by the YARP proxy.
/// </summary>
public static class SiteHostEndpoints
{
    public static IEndpointRouteBuilder MapSiteHost(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{**path}", async (
            string? path, HttpContext http, DomainResolver resolver,
            IObjectStorage storage, IOptions<StorageOptions> storageOptions, CancellationToken ct) =>
        {
            var route = await resolver.ResolveAsync(http.Request.Host.Value ?? string.Empty, ct);
            if (route is null)
            {
                return Results.NotFound("No site is published for this domain.");
            }

            var fileName = MapPathToFile(path);
            var bucket = storageOptions.Value.SitesBucket;
            // A request for a concrete asset (it carries an extension) must resolve
            // to that asset or 404 — never the SPA index.html, which would be served
            // with the asset's content-type (e.g. a missing .js → HTML parsed as JS).
            var isAsset = !string.IsNullOrWhiteSpace(path) && Path.HasExtension(path);

            var bytes = await TryGet(storage, bucket, $"{route.ArtifactPrefix}/{fileName}", ct);
            if (bytes is null && !isAsset)
            {
                bytes = await TryGet(storage, bucket, $"{route.ArtifactPrefix}/index.html", ct);
            }
            if (bytes is null)
            {
                return Results.NotFound();
            }

            var contentType = StaticSiteFiles.ContentTypeFor(fileName);
            http.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Bytes(bytes, contentType);
        });

        return app;
    }

    private static string MapPathToFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return "index.html";
        }
        // Static asset with an extension → serve as-is; otherwise treat as a page route.
        return Path.HasExtension(path) ? path : $"{path.Trim('/').Replace('/', '_')}.html";
    }

    private static async Task<byte[]?> TryGet(IObjectStorage storage, string bucket, string key, CancellationToken ct)
    {
        try
        {
            await using var stream = await storage.GetAsync(bucket, key, ct);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            return null;
        }
    }
}
