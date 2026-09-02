extern alias AdminApiApp;
using AdminApiApp::Dcms.AdminApi.Social;
using Dcms.Plugins.Facebook;
using Dcms.Plugins.Instagram;
using Dcms.Shared.Data.Social;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dcms.IntegrationTests.Social;

/// <summary>
/// The sync caps, tested where they actually have to hold.
///
/// <para>Every assertion here is about <i>requests</i>, not stored rows. An implementation
/// that downloads an entire Instagram history and then keeps the newest five satisfies any
/// row-count check while doing exactly the thing the caps exist to prevent — so a row count
/// is not evidence, and a request count is.</para>
/// </summary>
public class MetaFeedFetcherTests
{
    /// <summary>A stub account with effectively unlimited history, paging 100 at a time.</summary>
    private static async Task<(WebApplication Stub, MetaFeedFetcher Fetcher, List<int> Limits)> StubAsync(
        int totalItems, CancellationToken ct, Func<int, string>? productTypeFor = null)
    {
        var limits = new List<int>();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        app.MapGet("/{version}/{id}/media", (HttpContext http) => Page(http, totalItems, limits, productTypeFor));
        app.MapGet("/{id}/media", (HttpContext http) => Page(http, totalItems, limits, productTypeFor));
        app.MapGet("/{version}/{id}/published_posts", (HttpContext http) => Page(http, totalItems, limits, productTypeFor));

        await app.StartAsync(ct);

        var options = Options.Create(new MetaSocialOptions
        {
            Meta = new MetaAppOptions { AppId = "a", AppSecret = "b" },
            GraphVersion = "v25.0",
            OverrideBaseUrl = app.Urls.First(),
        });
        var graph = new MetaGraphClient(new HttpClient(), options, NullLogger<MetaGraphClient>.Instance);
        return (app, new MetaFeedFetcher(graph, NullLogger<MetaFeedFetcher>.Instance), limits);
    }

    private static IResult Page(HttpContext http, int total, List<int> limits, Func<int, string>? productTypeFor)
    {
        var limit = int.TryParse(http.Request.Query["limit"], out var l) ? l : 25;
        var offset = int.TryParse(http.Request.Query["after"], out var a) ? a : 0;
        limits.Add(limit);

        var take = Math.Min(limit, Math.Max(0, total - offset));
        var data = Enumerable.Range(offset, take).Select(i => new
        {
            id = $"m{i}",
            caption = $"Post {i}",
            media_type = "IMAGE",
            media_product_type = productTypeFor?.Invoke(i) ?? "FEED",
            media_url = $"https://cdn.test/{i}.jpg",
            permalink = $"https://instagram.test/p/{i}",
            timestamp = DateTimeOffset.UtcNow.AddMinutes(-i),
            username = "acme",
            message = $"Post {i}",
            created_time = DateTimeOffset.UtcNow.AddMinutes(-i),
            full_picture = $"https://cdn.test/{i}.jpg",
        }).ToArray();

        var next = offset + take;
        return Results.Json(new
        {
            data,
            paging = next < total ? new { cursors = new { after = next.ToString() } } : null,
        });
    }

    private static MetaConnection Connection(MetaProvider provider = MetaProvider.Facebook) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        Provider = provider,
        ExternalAccountId = "acct-1",
    };

    private static MetaFeedSettings Settings(int maxPosts, int maxReels = 0) => new(
        ConnectionId: Guid.NewGuid(),
        MaxPosts: maxPosts,
        MaxReels: maxReels,
        MediaTypes: new HashSet<string>(["photo", "carousel", "video", "link", "text"], StringComparer.OrdinalIgnoreCase),
        SyncSince: null,
        MirrorVideo: false,
        ShowStories: false,
        SyncIntervalMinutes: 15);

    [Fact]
    public async Task A_small_cap_costs_a_single_request_against_a_huge_account()
    {
        var ct = TestContext.Current.CancellationToken;
        var (stub, fetcher, limits) = await StubAsync(totalItems: 500, ct);

        try
        {
            var harvest = await fetcher.FetchAsync(
                Connection(), "TOKEN", InstagramPlugin.PluginId, Settings(maxPosts: 5), ct);

            harvest.Items.Should().HaveCount(5);

            // The claim the caps actually make. 500 items at 100 per page is 5 requests for a
            // fetch-then-trim implementation; the correct one asks once.
            harvest.PagesFetched.Should().Be(1);
            limits.Should().ContainSingle().Which.Should().Be(5,
                "the request should ask for exactly what is still wanted, not a full page");
        }
        finally
        {
            await stub.StopAsync(ct);
        }
    }

    [Fact]
    public async Task Reels_and_posts_are_budgeted_separately()
    {
        var ct = TestContext.Current.CancellationToken;
        // Every third item is a reel, so a shared counter would fill on reels and starve posts.
        var (stub, fetcher, _) = await StubAsync(
            totalItems: 300, ct, productTypeFor: i => i % 3 == 0 ? "REELS" : "FEED");

        try
        {
            var harvest = await fetcher.FetchAsync(
                Connection(), "TOKEN", InstagramPlugin.PluginId, Settings(maxPosts: 10, maxReels: 2), ct);

            var reels = harvest.Items.Count(i => i.IsReel);
            var posts = harvest.Items.Count(i => !i.IsReel);

            reels.Should().Be(2);
            posts.Should().Be(10, "a burst of reels must not crowd out the posts the site asked for");
        }
        finally
        {
            await stub.StopAsync(ct);
        }
    }

    [Fact]
    public async Task Caps_of_zero_make_no_request_at_all()
    {
        var ct = TestContext.Current.CancellationToken;
        var (stub, fetcher, limits) = await StubAsync(totalItems: 100, ct);

        try
        {
            var harvest = await fetcher.FetchAsync(
                Connection(), "TOKEN", InstagramPlugin.PluginId, Settings(maxPosts: 0, maxReels: 0), ct);

            harvest.Items.Should().BeEmpty();
            // Turning a feed off should stop the traffic, not just discard the answer.
            harvest.PagesFetched.Should().Be(0);
            limits.Should().BeEmpty();
        }
        finally
        {
            await stub.StopAsync(ct);
        }
    }

    [Fact]
    public async Task An_account_with_less_than_the_cap_is_not_paged_forever()
    {
        var ct = TestContext.Current.CancellationToken;
        var (stub, fetcher, _) = await StubAsync(totalItems: 3, ct);

        try
        {
            var harvest = await fetcher.FetchAsync(
                Connection(), "TOKEN", FacebookPlugin.PluginId, Settings(maxPosts: 50), ct);

            harvest.Items.Should().HaveCount(3);
            // Meta stops returning a cursor; the loop has to notice rather than spin to its
            // page ceiling asking for items that do not exist.
            harvest.PagesFetched.Should().Be(1);
        }
        finally
        {
            await stub.StopAsync(ct);
        }
    }

    [Fact]
    public async Task A_date_floor_excludes_older_posts_without_storing_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var (stub, fetcher, _) = await StubAsync(totalItems: 200, ct);

        try
        {
            // The stub ages each item by one minute, so only the first ten are inside an
            // eleven-minute window.
            var settings = Settings(maxPosts: 50) with { SyncSince = DateTimeOffset.UtcNow.AddMinutes(-11) };

            var harvest = await fetcher.FetchAsync(
                Connection(), "TOKEN", InstagramPlugin.PluginId, settings, ct);

            harvest.Items.Should().HaveCountLessThanOrEqualTo(12);
            harvest.Items.Should().OnlyContain(i => i.PostedAt >= settings.SyncSince);
        }
        finally
        {
            await stub.StopAsync(ct);
        }
    }
}
