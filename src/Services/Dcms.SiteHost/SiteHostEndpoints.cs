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

            var candidates = CandidatesFor(path);
            var fileName = candidates[0];
            var bucket = storageOptions.Value.SitesBucket;
            // A request for a concrete asset (it carries an extension) must resolve
            // to that asset or 404 — never the SPA index.html, which would be served
            // with the asset's content-type (e.g. a missing .js → HTML parsed as JS).
            var isAsset = !string.IsNullOrWhiteSpace(path) && Path.HasExtension(path);

            byte[]? bytes = null;
            foreach (var candidate in candidates)
            {
                bytes = await TryGet(storage, bucket, $"{route.ArtifactPrefix}/{candidate}", ct);
                if (bytes is not null) break;
            }
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

    /// <summary>
    /// The artifacts that could answer this request, most specific first.
    ///
    /// The second candidate is what makes a detail page work. A Mode A site can
    /// publish one page for a whole collection — <c>/events/:slug</c>, built to
    /// <c>events_@.html</c> — whose components read the last URL segment to know
    /// which item to show. So <c>/events/summer-party</c> asks for its own page
    /// first (an author may well have published a real one), then for the
    /// wildcard page that serves every event. Only if neither exists does the
    /// SPA-style index.html fallback apply, as before.
    ///
    /// Mirrors StaticSiteAssembler.FileNameFor — a change to one belongs in both,
    /// or the host asks for a file name the build never wrote.
    /// </summary>
    internal static IReadOnlyList<string> CandidatesFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return ["index.html"];
        }
        // Static asset with an extension → serve as-is; otherwise treat as a page route.
        if (Path.HasExtension(path))
        {
            return [path];
        }

        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return ["index.html"];
        }

        var exact = $"{string.Join('_', segments)}.html";
        // Only the last segment is wildcarded: a detail route names one item, and
        // matching further up would let /a/b/c quietly render /a's item page.
        var wildcard = $"{string.Join('_', segments[..^1].Append("@"))}.html";
        return exact == wildcard ? [exact] : [exact, wildcard];
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
