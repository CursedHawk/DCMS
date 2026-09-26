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
            string? path, HttpContext http, DomainResolver resolver, SiteArtifactCache cache,
            IObjectStorage storage, IOptions<StorageOptions> storageOptions, CancellationToken ct) =>
        {
            var route = await resolver.ResolveAsync(http.Request.Host.Value ?? string.Empty, ct);
            if (route is null)
            {
                return Results.NotFound("No site is published for this domain.");
            }
            return await ServeAsync(http, path, route.ArtifactPrefix, cache, storage, storageOptions.Value.SitesBucket, ct);
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
    /// <summary>
    /// Serves one path of a resolved build. Small files come from <see cref="SiteArtifactCache"/>;
    /// large ones, and any Range request, stream from storage as before -- still skipping the
    /// candidate lookups, since the cache already knows which object the path is.
    /// </summary>
    internal static async Task<IResult> ServeAsync(
        HttpContext http, string? path, string artifactPrefix, SiteArtifactCache cache,
        IObjectStorage storage, string bucket, CancellationToken ct)
    {
        var candidates = CandidatesFor(path);
        var isAsset = !string.IsNullOrWhiteSpace(path) && Path.HasExtension(path);
        var entry = await cache.GetAsync(storage, bucket, artifactPrefix, path ?? string.Empty, candidates, indexFallback: !isAsset, ct);
        if (entry.Key is null)
        {
            return Results.NotFound();
        }

        var contentType = StaticSiteFiles.ContentTypeFor(candidates[0]);
        http.Response.Headers.CacheControl = "public, max-age=60";
        if (entry.Body is not { } body || !string.IsNullOrEmpty(http.Request.Headers.Range))
        {
            return await ObjectStreaming.WriteObjectAsync(http, storage, bucket, entry.Key, contentType, ct);
        }

        http.Response.Headers.AcceptRanges = "bytes";
        http.Response.ContentType = contentType;
        http.Response.ContentLength = body.Length;
        if (!HttpMethods.IsHead(http.Request.Method))
        {
            await http.Response.Body.WriteAsync(body, ct);
        }
        return Results.Empty;
    }

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

}
