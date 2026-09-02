using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Plugins.Instagram;
using Dcms.Plugins.Meta.Core;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Social;

/// <summary>
/// A sync end to end: Meta stub → Graph read → media mirror → content items → retention.
///
/// <para><see cref="MetaFeedFetcherTests"/> already proves the caps bound the number of
/// requests. These tests are about what happens to the results afterwards — what is stored,
/// what is <i>not</i> downloaded a second time, and what a trim is allowed to delete. Each of
/// those is a claim that only shows up in production if it is wrong: a mirror that re-downloads
/// looks fine until the rate limit, and a trim that over-deletes looks fine until it takes an
/// admin's own upload with it.</para>
/// </summary>
[Collection(AdminApiCollection.Name)]
public class MetaSyncTests(AdminApiFixture fixture)
{
    private static readonly DateTimeOffset Newest = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [DockerFact]
    public async Task A_sync_stores_the_capped_number_of_posts_and_mirrors_their_images()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        var t = await ConnectedInstanceAsync(client, ct, maxPosts: 5, feedSize: 40);

        var outcome = await SyncAsync(client, t, ct);

        outcome.GetProperty("created").GetInt32().Should().Be(5);

        var items = await ContentAsync(client, t, ct);
        items.Should().HaveCount(5);
        items.Should().AllSatisfy(i => i.GetProperty("status").GetString().Should().Be("Published"));

        // Newest first: the cap keeps the top of the feed, not an arbitrary five.
        items.Select(i => i.GetProperty("slug").GetString())
            .Should().BeEquivalentTo(["m0", "m1", "m2", "m3", "m4"]);

        // Every kept post's image is a DCMS asset, not a Meta CDN link. Meta's media URLs are
        // signed and expire, so a stored link is a picture that works today and breaks silently
        // next week — the mirrored id is the only durable answer.
        var data = await ItemDataAsync(t, "m0", ct);
        data.GetProperty("media").GetString().Should().NotBeNullOrEmpty();
        Guid.TryParse(data.GetProperty("media").GetString(), out var assetId).Should().BeTrue();
        data.GetProperty("mediaUrl").ValueKind.Should().Be(
            JsonValueKind.Null, "a mirrored post must not also carry the expiring CDN link");

        (await AssetExistsAsync(assetId, ct)).Should().BeTrue();
    }

    [DockerFact]
    public async Task A_second_sync_downloads_nothing_and_creates_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        var t = await ConnectedInstanceAsync(client, ct, maxPosts: 4, feedSize: 10);

        await SyncAsync(client, t, ct);
        var afterFirst = fixture.MetaStub.CdnDownloads;
        afterFirst.Should().BeGreaterThan(0, "the first sync has to actually fetch the images");

        var second = await SyncAsync(client, t, ct);

        second.GetProperty("created").GetInt32().Should().Be(0);
        // The claim that makes a fifteen-minute poll affordable. Without meta_media_map every
        // pass would re-download and re-encode the whole feed, for every tenant, forever.
        fixture.MetaStub.CdnDownloads.Should().Be(
            afterFirst, "a re-sync of unchanged posts must not touch the CDN at all");

        (await ContentAsync(client, t, ct)).Should().HaveCount(4);
    }

    [DockerFact]
    public async Task Raising_a_cap_backfills_and_lowering_it_trims_the_oldest()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        var t = await ConnectedInstanceAsync(client, ct, maxPosts: 3, feedSize: 20);

        await SyncAsync(client, t, ct);
        (await ContentAsync(client, t, ct)).Should().HaveCount(3);

        await SetMaxPostsAsync(client, t, 6, ct);
        await SyncAsync(client, t, ct);

        var backfilled = await ContentAsync(client, t, ct);
        backfilled.Should().HaveCount(6);
        // The three added on the second pass are older than the three already stored. They are
        // inserted last, so a sync that stamped "now" as the publish date would file them as
        // the newest posts on the site and sort the feed backwards.
        backfilled.Select(i => i.GetProperty("slug").GetString())
            .Should().BeEquivalentTo(["m0", "m1", "m2", "m3", "m4", "m5"]);
        (await PublishedAtAsync(client, t, "m0", ct))
            .Should().BeAfter(await PublishedAtAsync(client, t, "m5", ct),
                "items must be dated by when Meta posted them, not by when we happened to fetch them");

        await SetMaxPostsAsync(client, t, 2, ct);
        var trimmed = await SyncAsync(client, t, ct);

        trimmed.GetProperty("trimmed").GetInt32().Should().Be(4);
        (await ContentAsync(client, t, ct)).Select(i => i.GetProperty("slug").GetString())
            .Should().BeEquivalentTo(["m0", "m1"], "a trim drops the oldest, never the newest");
    }

    [DockerFact]
    public async Task A_trim_deletes_only_the_assets_the_sync_created()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        var t = await ConnectedInstanceAsync(client, ct, maxPosts: 3, feedSize: 10);

        await SyncAsync(client, t, ct);

        // An ordinary admin upload, sitting in the same media library the sync writes into.
        var ownUpload = await UploadAsync(client, t, ct);
        var doomed = Guid.Parse((await ItemDataAsync(t, "m2", ct)).GetProperty("media").GetString()!);

        await SetMaxPostsAsync(client, t, 1, ct);
        await SyncAsync(client, t, ct);

        (await AssetExistsAsync(doomed, ct)).Should().BeFalse("the trimmed post's mirrored image is orphaned");
        // The reason retention is scoped through meta_media_map rather than by "assets in this
        // tenant that this sync could have touched". Getting this wrong deletes a customer's
        // own photographs, and it would do so quietly.
        (await AssetExistsAsync(ownUpload, ct)).Should().BeTrue("an admin's own upload is not the sync's to delete");
    }

    [DockerFact]
    public async Task An_item_the_admin_unpublished_stays_hidden_across_a_sync()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        var t = await ConnectedInstanceAsync(client, ct, maxPosts: 4, feedSize: 10);

        await SyncAsync(client, t, ct);
        var hidden = (await ContentAsync(client, t, ct))
            .Single(i => i.GetProperty("slug").GetString() == "m1").GetProperty("id").GetGuid();

        var unpublish = await client.SendAsync(
            Req(HttpMethod.Post, $"/api/admin/content/{hidden}/unpublish", t.Owner, t.Slug), ct);
        unpublish.EnsureSuccessStatusCode();

        // Meta changes the caption, so the item genuinely has an update to apply — otherwise
        // the sync would skip it and the test would pass without exercising the hide path.
        fixture.MetaStub.Media[1] = fixture.MetaStub.Post("m1", "edited caption", Newest.AddDays(-1));
        await SyncAsync(client, t, ct);

        var after = (await ContentAsync(client, t, ct)).Single(i => i.GetProperty("slug").GetString() == "m1");
        after.GetProperty("status").GetString().Should().Be(
            "Draft", "unpublish is the hide toggle; a sync that republished it would make the control useless");
    }

    [DockerFact]
    public async Task A_cap_above_the_platform_ceiling_is_refused_by_the_server_not_just_the_form()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        var t = await ConnectedInstanceAsync(client, ct, maxPosts: 5, feedSize: 5);

        var res = await client.SendAsync(Req(
            HttpMethod.Put, $"/api/admin/plugins/instances/{t.InstanceId}", t.Owner, t.Slug,
            body: new { config = Config(t.ConnectionId, MetaFeedConfig.MaxItemsCeiling + 1) }), ct);

        // The JSON Schema's `maximum` is the browser's story. The config endpoint takes JSON
        // from anywhere, so the ceiling only means something if this request is the one refused.
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(ct)).Should().Contain(MetaFeedConfig.MaxPostsKey);
    }

    // ---------- helpers ----------

    private sealed record Tenant(Guid Owner, string Slug, Guid ConnectionId, Guid InstanceId);

    private HttpClient Client() => fixture.Factory.CreateClient(
        new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// A tenant with a connected Instagram account and an installed, configured instance,
    /// plus a stub feed of <paramref name="feedSize"/> posts, newest first.
    /// </summary>
    private async Task<Tenant> ConnectedInstanceAsync(
        HttpClient client, CancellationToken ct, int maxPosts, int feedSize)
    {
        var owner = Guid.NewGuid();
        var slug = "sync-" + Guid.NewGuid().ToString("N")[..8];

        var provision = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", owner, null,
            body: new { slug, name = slug, ownerUserId = owner, ownerEmail = "o@dcms.test" },
            asSuperAdmin: true), ct);
        provision.EnsureSuccessStatusCode();

        fixture.MetaStub.Reset();
        fixture.MetaStub.Pages.Add(
            MetaStubServer.Page("page-1", "Acme Bakery", "PAGE-TOKEN", "ig-1", "acmebakery"));

        // Newest first, one day apart, exactly as the feed edge returns them.
        for (var i = 0; i < feedSize; i++)
        {
            fixture.MetaStub.Media.Add(fixture.MetaStub.Post($"m{i}", $"Post {i}", Newest.AddDays(-i)));
        }

        var connectRes = await client.SendAsync(
            Req(HttpMethod.Get, "/api/admin/social/facebook/connect", owner, slug), ct);
        connectRes.EnsureSuccessStatusCode();
        var authorizeUrl = (await connectRes.Content.ReadFromJsonAsync<JsonElement>(ct))
            .GetProperty("authorizeUrl").GetString()!;
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(authorizeUrl).Query)["state"]!;

        var callback = await client.GetAsync($"/api/admin/social/callback?code=auth-code&state={state}", ct);
        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var connections = (await (await client.SendAsync(
                Req(HttpMethod.Get, "/api/admin/social/connections", owner, slug), ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().ToList();

        // The Instagram account behind the Page — the Page itself is a facebook connection and
        // is not what an instagram instance pulls from.
        var connectionId = connections
            .Single(c => c.GetProperty("provider").GetString() == nameof(MetaProvider.Facebook)
                         && c.GetProperty("accountUsername").GetString() == "acmebakery")
            .GetProperty("id").GetGuid();

        var create = await client.SendAsync(Req(
            HttpMethod.Post, "/api/admin/plugins/instances", owner, slug,
            body: new
            {
                pluginId = InstagramPlugin.PluginId,
                slug = "feed",
                name = "Feed",
                config = Config(connectionId, maxPosts),
            }), ct);
        create.EnsureSuccessStatusCode();

        var instanceId = (await create.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        return new Tenant(owner, slug, connectionId, instanceId);
    }

    /// <summary>Reels are off throughout: these tests are about feed posts and retention.</summary>
    private static string Config(Guid connectionId, int maxPosts) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        [MetaFeedConfig.ConnectionIdKey] = connectionId.ToString(),
        [MetaFeedConfig.MaxPostsKey] = maxPosts,
        [MetaFeedConfig.MaxReelsKey] = 0,
        [MetaFeedConfig.MirrorVideoKey] = false,
    });

    private async Task SetMaxPostsAsync(HttpClient client, Tenant t, int maxPosts, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(
            HttpMethod.Put, $"/api/admin/plugins/instances/{t.InstanceId}", t.Owner, t.Slug,
            body: new { config = Config(t.ConnectionId, maxPosts) }), ct);
        res.EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> SyncAsync(HttpClient client, Tenant t, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(
            HttpMethod.Post, $"/api/admin/social/instances/{t.InstanceId}/sync", t.Owner, t.Slug), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, "body was: {0}", await res.Content.ReadAsStringAsync(ct));
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task<List<JsonElement>> ContentAsync(HttpClient client, Tenant t, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(
            HttpMethod.Get,
            $"/api/admin/content?instanceId={t.InstanceId}&contentType={InstagramPlugin.PostType}",
            t.Owner, t.Slug), ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().ToList();
    }

    private async Task<DateTimeOffset> PublishedAtAsync(
        HttpClient client, Tenant t, string slug, CancellationToken ct) =>
        (await ContentAsync(client, t, ct))
        .Single(i => i.GetProperty("slug").GetString() == slug)
        .GetProperty("publishedAt").GetDateTimeOffset();

    /// <summary>
    /// The stored payload of one item. Read straight from the database rather than through the
    /// admin API, because the mirrored asset id lives inside the version JSON.
    /// </summary>
    private async Task<JsonElement> ItemDataAsync(Tenant t, string itemSlug, CancellationToken ct)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var cms = scope.ServiceProvider.GetRequiredService<CmsDbContext>();

        var json = await cms.ContentItems.IgnoreQueryFilters()
            .Where(c => c.Slug == itemSlug && c.PluginInstanceId == t.InstanceId)
            .Join(cms.ContentVersions.IgnoreQueryFilters(),
                c => c.PublishedVersionId, v => v.Id, (_, v) => v.DataJson)
            .FirstAsync(ct);

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private async Task<bool> AssetExistsAsync(Guid assetId, CancellationToken ct)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var media = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
        return await media.Assets.IgnoreQueryFilters().AnyAsync(a => a.Id == assetId, ct);
    }

    /// <summary>An ordinary admin upload, through the real upload endpoint.</summary>
    private static async Task<Guid> UploadAsync(HttpClient client, Tenant t, CancellationToken ct)
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "mine.png");

        var req = Req(HttpMethod.Post, "/api/admin/media", t.Owner, t.Slug);
        req.Content = form;

        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
    }

    private static HttpRequestMessage Req(
        HttpMethod method, string url, Guid sub, string? slug, object? body = null, bool asSuperAdmin = false)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (asSuperAdmin) req.Headers.Add("X-Test-Roles", "SuperAdmin");
        if (slug is not null) req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }
}
