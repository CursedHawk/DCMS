using System.Runtime.CompilerServices;
using System.Text.Json;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Sites;
using Dcms.Shared.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Dcms.SiteHost;

/// <summary>
/// <c>/sitemap.xml</c> and <c>/robots.txt</c> for every hosted site, out of the box (#343). A file
/// of the site's own always wins. Otherwise the sitemap lists the build's pages
/// (<see cref="SitemapPages.Key"/>) under the domain asked, each detail page once per published
/// item, and robots.txt points to it. A branch's domain (#277) has neither: it asks not to be indexed.
/// </summary>
public static class SitemapEndpoints
{
    /// <summary>How long a made sitemap is kept: each costs a content-api call per page of items.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private const int ItemPageSize = 100;

    /// <summary>The named client the sitemap lists published items with.</summary>
    public const string ContentApiClient = "content-api";

    public static IServiceCollection AddSitemap(this IServiceCollection services, string contentApiBaseUrl)
    {
        services.AddHttpClient(ContentApiClient, client =>
        {
            client.BaseAddress = new Uri(contentApiBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(15) });
        return services;
    }

    public static IEndpointRouteBuilder MapSitemap(this IEndpointRouteBuilder app)
    {
        app.MapGet("/sitemap.xml", async (
            HttpContext http, DomainResolver resolver, SiteArtifactCache cache, IObjectStorage storage,
            IOptions<StorageOptions> options, IMemoryCache memory, IHttpClientFactory clients, ILoggerFactory logs, CancellationToken ct) =>
        {
            var logger = logs.CreateLogger("Dcms.SiteHost.Sitemap");
            var route = await resolver.ResolveAsync(http.Request.Host.Value ?? string.Empty, ct);
            // A branch's domain, or a site its people hide from search engines (#648): no sitemap.
            if (route is null || !route.Indexable)
            {
                return Results.NotFound();
            }
            var bucket = options.Value.SitesBucket;
            if (await OwnAsync(http, "sitemap.xml", route, cache, storage, bucket, ct) is { } own)
            {
                return own;
            }

            var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
            var gated = http.Request.Headers[SitemapPages.GatedPrefixesHeader].ToString();
            var host = http.Request.Host.Host;
            byte[]? xml;
            try
            {
                // A failure throws, so nothing is cached: a half-listed sitemap is not kept for ten minutes.
                xml = await memory.GetOrCreateAsync(("sitemap.xml", route.ArtifactPrefix, baseUrl, gated, route.Search.ToJson()), async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = Lifetime;
                    var pages = await PagesAsync(route.ArtifactPrefix, cache, storage, bucket, ct);
                    var client = clients.CreateClient(ContentApiClient);
                    return await SitemapXml.WriteAsync(baseUrl, pages, SitemapXml.Prefixes(gated),
                        page => ItemsAsync(client, route, host, page, logger, ct), route.Search, ct);
                });
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(e, "Sitemap of {Host}: published items could not be listed.", host);
                http.Response.Headers.RetryAfter = "60";
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            http.Response.Headers.CacheControl = "public, max-age=300";
            return Results.Bytes(xml!, "application/xml; charset=utf-8");
        });

        app.MapGet("/robots.txt", async (
            HttpContext http, DomainResolver resolver, SiteArtifactCache cache, IObjectStorage storage,
            IOptions<StorageOptions> options, CancellationToken ct) =>
        {
            var route = await resolver.ResolveAsync(http.Request.Host.Value ?? string.Empty, ct);
            if (route is null)
            {
                return Results.NotFound();
            }
            http.Response.Headers.CacheControl = "public, max-age=3600";
            if (!route.Indexable)
            {
                return Results.Text("User-agent: *\nDisallow: /\n", "text/plain; charset=utf-8");
            }
            return await OwnAsync(http, "robots.txt", route, cache, storage, options.Value.SitesBucket, ct)
                ?? Results.Text($"User-agent: *\nAllow: /\n\nSitemap: {http.Request.Scheme}://{http.Request.Host}/sitemap.xml\n", "text/plain; charset=utf-8");
        });

        return app;
    }

    /// <summary>The site's own file of that name, when its build has one.</summary>
    private static async Task<IResult?> OwnAsync(
        HttpContext http, string name, SiteRoute route, SiteArtifactCache cache, IObjectStorage storage, string bucket, CancellationToken ct)
    {
        var entry = await cache.GetAsync(storage, bucket, route.ArtifactPrefix, name, [name], indexFallback: false, ct);
        return entry.Key is null ? null
            : await SiteHostEndpoints.ServeAsync(http, name, route.ArtifactPrefix, cache, storage, bucket, ct);
    }

    /// <summary>The build's page list; a build from before it existed lists its root alone.</summary>
    private static async Task<IReadOnlyList<SitemapPage>> PagesAsync(
        string artifactPrefix, SiteArtifactCache cache, IObjectStorage storage, string bucket, CancellationToken ct)
    {
        var entry = await cache.GetAsync(storage, bucket, artifactPrefix, SitemapPages.Key, [SitemapPages.Key], indexFallback: false, ct);
        if (entry.Key is null)
        {
            return [new SitemapPage("/")];
        }
        var bytes = entry.Body;
        if (bytes is null)
        {
            using var buffer = new MemoryStream();
            await storage.GetToAsync(bucket, entry.Key, buffer, ct: ct);
            bytes = buffer.ToArray();
        }
        try
        {
            return SitemapPages.Parse(bytes);
        }
        catch (JsonException)
        {
            return [new SitemapPage("/")];
        }
    }

    /// <summary>
    /// A detail page's published items, as a visitor's browser lists them: the same public route,
    /// so content a visitor must sign in for is refused here too and left out.
    /// </summary>
    private static async IAsyncEnumerable<(string Slug, DateTimeOffset? PublishedAt)> ItemsAsync(
        HttpClient client, SiteRoute route, string host, SitemapPage page, ILogger logger, [EnumeratorCancellation] CancellationToken ct)
    {
        var seen = 0;
        for (var number = 1; ; number++)
        {
            using var list = await ListAsync(client, route, host, page, number, logger, ct);
            if (list is null
                || !list.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
                || items.GetArrayLength() == 0)
            {
                yield break;
            }
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("slug", out var slug) && slug.GetString() is { Length: > 0 } value)
                {
                    yield return (value, item.TryGetProperty("publishedAt", out var at) && at.TryGetDateTimeOffset(out var when) ? when : null);
                }
            }
            seen += items.GetArrayLength();
            if (!list.RootElement.TryGetProperty("totalCount", out var total) || seen >= total.GetInt64())
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// One page of items; null when content-api says this collection is not for an anonymous
    /// visitor (or not there). Anything worse throws, and the sitemap is not made this time.
    /// </summary>
    private static async Task<JsonDocument?> ListAsync(
        HttpClient client, SiteRoute route, string host, SitemapPage page, int number, ILogger logger, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/{Uri.EscapeDataString(page.Instance!)}/{Uri.EscapeDataString(page.ContentType!)}?page={number}&pageSize={ItemPageSize}");
        ApiProxy.SetSiteHeaders(request.Headers, route, host);
        using var response = await client.SendAsync(request, ct);
        if ((int)response.StatusCode is >= 400 and < 500)
        {
            logger.LogDebug("Sitemap of {Host}: {Path} lists no items ({Status}).", host, page.Path, (int)response.StatusCode);
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }
}
