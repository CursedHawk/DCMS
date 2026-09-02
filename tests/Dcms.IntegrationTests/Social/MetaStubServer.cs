using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dcms.IntegrationTests.Social;

/// <summary>
/// A stand-in for the Meta Graph API, served in-process on a dynamic port.
///
/// <para>It exists because every real call carries the platform app secret and a tenant's
/// access token, so the OAuth and sync paths can never be exercised against Meta itself. The
/// request log it keeps is not incidental: the sync caps are a claim about how many requests
/// are made, and only a counted stub can hold that claim honest.</para>
/// </summary>
public sealed class MetaStubServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private MetaStubServer(WebApplication app) => _app = app;

    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>Every path this stub was asked for, in order, across all tests.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Pages returned by <c>/me/accounts</c>. A test sets this before connecting.</summary>
    public List<object> Pages { get; } = [];

    /// <summary>
    /// The account's media, newest first, as the feed edges return it. A test sets this
    /// before syncing; <see cref="Feed"/> builds the nodes.
    /// </summary>
    public List<object> Media { get; } = [];

    /// <summary>
    /// How many times a media file was actually downloaded from the "CDN".
    ///
    /// <para>The number this feature lives or dies on. A re-sync every fifteen minutes is only
    /// affordable because the second pass over a post downloads nothing, and the only way to
    /// tell a working media map from a broken one is to count the fetches.</para>
    /// </summary>
    public int CdnDownloads => _cdnDownloads;

    private int _cdnDownloads;

    public void ResetCdnDownloads() => Interlocked.Exchange(ref _cdnDownloads, 0);

    public int RequestsFor(string pathFragment) =>
        Requests.Count(p => p.Contains(pathFragment, StringComparison.Ordinal));

    public void Reset()
    {
        Requests.Clear();
        Pages.Clear();
        Media.Clear();
        Stories.Clear();
        ResetCdnDownloads();
    }

    public static async Task<MetaStubServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        var stub = new MetaStubServer(app);

        app.Use(async (http, next) =>
        {
            stub.Requests.Enqueue(http.Request.Path.Value ?? string.Empty);
            await next();
        });

        // Token endpoints. The version segment is a wildcard so a GraphVersion bump in
        // configuration does not silently stop matching and leave every test 404-ing.
        app.MapGet("/{version}/oauth/access_token", (HttpContext http) => Results.Json(new
        {
            access_token = http.Request.Query["grant_type"] == "fb_exchange_token"
                ? "LONG-LIVED-USER-TOKEN"
                : "SHORT-LIVED-USER-TOKEN",
            token_type = "bearer",
            expires_in = 5_184_000,
        }));

        app.MapGet("/{version}/me/accounts", () => Results.Json(new { data = stub.Pages }));

        // The feed edges. Both hosts are the same stub, so the Instagram-Login path
        // (graph.instagram.com, no version segment) and the Page path both have to match.
        app.MapGet("/{version}/{id}/media", (HttpContext http) => stub.FeedPage(http));
        app.MapGet("/{id}/media", (HttpContext http) => stub.FeedPage(http));
        app.MapGet("/{version}/{id}/published_posts", (HttpContext http) => stub.FeedPage(http));

        // Stories are never paged: Meta expires them after 24 hours and returns the handful
        // that are live.
        app.MapGet("/{version}/{id}/stories", () => Results.Json(new { data = stub.Stories }));

        // Stands in for Meta's media CDN. Returns a real PNG rather than arbitrary bytes
        // because the mirror runs everything through the image sanitizer, which re-encodes --
        // bytes that are not an image would be rejected and the test would pass for the
        // wrong reason.
        app.MapGet("/cdn/{file}", () =>
        {
            Interlocked.Increment(ref stub._cdnDownloads);
            return Results.File(OnePixelPng, "image/png");
        });

        app.MapPost("/oauth/access_token", () => Results.Json(new
        {
            access_token = "IG-SHORT-TOKEN", user_id = "ig-user-1",
        }));
        app.MapGet("/access_token", () => Results.Json(new
        {
            access_token = "IG-LONG-TOKEN", token_type = "bearer", expires_in = 5_184_000,
        }));
        app.MapGet("/me", () => Results.Json(new
        {
            id = "ig-user-1", username = "standalone", name = "Standalone Creator",
            profile_picture_url = "https://cdn.test/ig.jpg",
        }));

        await app.StartAsync();
        stub.BaseUrl = app.Urls.First();
        return stub;
    }

    /// <summary>The account's live stories. Empty unless a test sets them.</summary>
    public List<object> Stories { get; } = [];

    /// <summary>
    /// One page of <see cref="Media"/>, honouring the <c>limit</c> and <c>after</c> the
    /// fetcher sends. The cursor is just an offset — enough to page correctly, and the
    /// fetcher treats it as opaque anyway.
    /// </summary>
    private IResult FeedPage(HttpContext http)
    {
        var limit = int.TryParse(http.Request.Query["limit"], out var l) ? l : 25;
        var offset = int.TryParse(http.Request.Query["after"], out var a) ? a : 0;

        var take = Math.Min(limit, Math.Max(0, Media.Count - offset));
        var data = Media.Skip(offset).Take(take).ToList();
        var next = offset + take;

        return Results.Json(new
        {
            data,
            paging = next < Media.Count
                ? new { cursors = new { after = next.ToString() } }
                : null,
        });
    }

    /// <summary>
    /// A feed node. <paramref name="productType"/> is <c>FEED</c> or <c>REELS</c> — the only
    /// thing that separates a reel from a feed video, and therefore which cap it counts
    /// against.
    ///
    /// <para>The media URL is absolute against this stub's own base address, because the
    /// mirror's <c>meta-cdn</c> client has no base address of its own — a real Meta node
    /// carries an absolute CDN link too.</para>
    /// </summary>
    public object Post(
        string id, string caption, DateTimeOffset postedAt,
        string mediaType = "IMAGE", string productType = "FEED", string? mediaUrl = null) =>
        new
        {
            id,
            caption,
            media_type = mediaType,
            media_product_type = productType,
            media_url = mediaUrl ?? $"{BaseUrl}/cdn/{id}.png",
            permalink = $"https://instagram.test/p/{id}",
            timestamp = postedAt.ToString("O"),
            username = "acmebakery",
        };

    /// <summary>
    /// A 1×1 PNG. Small enough to be inline, real enough to survive the sanitizer's
    /// decode-and-re-encode — which is the whole point of routing mirrored bytes through it.
    /// </summary>
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>A Page node in the shape <c>/me/accounts</c> returns, optionally with a linked IG account.</summary>
    public static object Page(string id, string name, string token, string? igId = null, string? igUsername = null) =>
        new
        {
            id,
            name,
            access_token = token,
            picture = new { data = new { url = $"https://cdn.test/{id}.jpg" } },
            instagram_business_account = igId is null
                ? null
                : (object)new
                {
                    id = igId, username = igUsername, name = igUsername,
                    profile_picture_url = $"https://cdn.test/{igId}.jpg",
                },
        };

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
