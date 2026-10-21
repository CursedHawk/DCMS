using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Plugins.YouTube;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// The YouTube sync end to end: stubbed Data API → Sync-now route → <c>youtube-video</c>
/// content. What it stores, what it leaves out, what it removes, and what it must never touch.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class YouTubeSyncTests(AdminApiFixture fixture)
{
    private const string ChannelId = "UCacmeacmeacmeacmeacme12";
    private const string Uploads = "UUacmeacmeacmeacmeacme12";
    private const string Shorts = "UUSHacmeacmeacmeacmeacme12";
    private static readonly DateTimeOffset Newest = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [DockerFact]
    public async Task A_sync_stores_the_newest_videos_up_to_the_cap_without_shorts_or_streams()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        Channel(uploads: 30, shorts: ["v1"], live: ["v2"]);
        var t = await InstanceAsync(client, new { channel = "https://www.youtube.com/@acme", maxVideos = 5 }, ct);

        var outcome = await SyncAsync(client, t, ct);

        outcome.GetProperty("created").GetInt32().Should().Be(5);
        (await SlugsAsync(client, t, ct)).Should().BeEquivalentTo(["v0", "v3", "v4", "v5", "v6"],
            "the Short (v1) and the stream (v2) are skipped and the cap is filled from further down");

        var data = await ItemDataAsync(t, "v0", ct);
        data.GetProperty("title").GetString().Should().Be("Video v0");
        data.GetProperty("url").GetString().Should().Be("https://www.youtube.com/watch?v=v0");
        data.GetProperty("embedUrl").GetString().Should().Be("https://www.youtube-nocookie.com/embed/v0");
        data.GetProperty("thumbnail").GetString().Should().Be("https://i.ytimg.test/vi/v0/hq.jpg");
        data.GetProperty("durationSeconds").GetInt32().Should().Be(65);
        data.GetProperty("viewCount").GetInt64().Should().Be(10);
        data.GetProperty("kind").GetString().Should().Be("video");

        // One page of uploads covers the cap: the sync must not keep reading the channel.
        fixture.YouTubeStub.RequestsFor("playlistItems?part=contentDetails&maxResults=50&playlistId=" + Uploads).Should().Be(1);
    }

    [DockerFact]
    public async Task Shorts_and_streams_are_kept_and_labelled_when_asked_for()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        Channel(uploads: 5, shorts: ["v1"], live: ["v2"]);
        var t = await InstanceAsync(client, new { channel = "@acme", maxVideos = 5, includeShorts = true, includeLive = true }, ct);

        await SyncAsync(client, t, ct);

        (await ItemDataAsync(t, "v1", ct)).GetProperty("kind").GetString().Should().Be("short");
        (await ItemDataAsync(t, "v2", ct)).GetProperty("kind").GetString().Should().Be("live");
    }

    [DockerFact]
    public async Task Resyncing_updates_in_place_and_drops_what_left_the_channel()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        Channel(uploads: 10);
        var t = await InstanceAsync(client, new { channel = ChannelId, maxVideos = 3 }, ct);
        await SyncAsync(client, t, ct);

        // Views change on every pass. That must not add a content version each time.
        fixture.YouTubeStub.Videos["v0"] = fixture.YouTubeStub.Videos["v0"] with { Views = 999 };
        // The newest video is deleted on YouTube; the next one moves into the window.
        fixture.YouTubeStub.Playlists[Uploads].Remove("v1");

        var second = await SyncAsync(client, t, ct);

        second.GetProperty("created").GetInt32().Should().Be(1);
        second.GetProperty("removed").GetInt32().Should().Be(1);
        (await SlugsAsync(client, t, ct)).Should().BeEquivalentTo(["v0", "v2", "v3"]);
        (await ItemDataAsync(t, "v0", ct)).GetProperty("viewCount").GetInt64().Should().Be(999);
        (await VersionCountAsync(t, "v0", ct)).Should().Be(1, "a synced video keeps one version, rewritten in place");
    }

    [DockerFact]
    public async Task The_console_titles_and_opens_a_synced_video_with_its_YouTube_data()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        Channel(uploads: 2);
        var t = await InstanceAsync(client, new { channel = "@acme", maxVideos = 2 }, ct);
        await SyncAsync(client, t, ct);

        // The collection list titles an item by its first text field: the title, not the id.
        var page = await client.SendAsync(Req(HttpMethod.Get,
            $"/api/admin/content/page?instanceId={t.InstanceId}&contentType={YouTubePlugin.VideoType}", t), ct);
        page.EnsureSuccessStatusCode();
        var rows = (await page.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items").EnumerateArray().ToList();
        rows.Select(r => r.GetProperty("title").GetString()).Should().BeEquivalentTo(["Video v0", "Video v1"]);

        // The editor shows the version the draft pointer names; a synced item without one opened empty.
        var id = rows.Single(r => r.GetProperty("slug").GetString() == "v0").GetProperty("id").GetGuid();
        var item = await (await client.SendAsync(Req(HttpMethod.Get, $"/api/admin/content/{id}", t), ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);
        var draft = item.GetProperty("draft");
        draft.GetProperty("title").GetString().Should().Be("Video v0");
        draft.GetProperty("description").GetString().Should().Be("About v0");
        draft.GetProperty("durationSeconds").GetInt32().Should().Be(65);
        draft.GetProperty("viewCount").GetInt64().Should().Be(10);
    }

    [DockerFact]
    public async Task An_unchanged_resync_writes_nothing_and_repairs_an_item_without_a_draft_pointer()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        Channel(uploads: 2);
        var t = await InstanceAsync(client, new { channel = "@acme", maxVideos = 2 }, ct);
        await SyncAsync(client, t, ct);
        var before = await ItemRowAsync(t, "v0", ct);

        // As the items synced before the sync set the pointer were left.
        await WithItemAsync(t, "v1", item => item.CurrentDraftVersionId = null, ct);
        await SyncAsync(client, t, ct);

        var after = await ItemRowAsync(t, "v0", ct);
        after.UpdatedAt.Should().Be(before.UpdatedAt, "jsonb's own key order is not a change to republish");
        (await ItemRowAsync(t, "v1", ct)).CurrentDraftVersionId.Should().NotBeNull();
    }

    [DockerFact]
    public async Task An_admins_edit_is_kept_as_its_own_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        Channel(uploads: 1);
        var t = await InstanceAsync(client, new { channel = "@acme", maxVideos = 1 }, ct);
        await SyncAsync(client, t, ct);
        var id = (await ContentAsync(client, t, ct)).Single().GetProperty("id").GetGuid();

        (await client.SendAsync(Req(HttpMethod.Put, $"/api/admin/content/{id}", t, new { data = new { title = "Mine" } }), ct))
            .EnsureSuccessStatusCode();
        fixture.YouTubeStub.Videos["v0"] = fixture.YouTubeStub.Videos["v0"] with { Views = 500 };
        await SyncAsync(client, t, ct);

        (await VersionCountAsync(t, "v0", ct)).Should().Be(3, "the sync adds its own version after the admin's instead of rewriting it");
        (await ItemDataAsync(t, "v0", ct)).GetProperty("viewCount").GetInt64().Should().Be(500);
    }

    [DockerFact]
    public async Task An_item_the_admin_unpublished_stays_hidden_across_a_sync()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        Channel(uploads: 4);
        var t = await InstanceAsync(client, new { channel = "@acme", maxVideos = 4 }, ct);
        await SyncAsync(client, t, ct);

        var hidden = (await ContentAsync(client, t, ct)).Single(i => i.GetProperty("slug").GetString() == "v1").GetProperty("id").GetGuid();
        (await client.SendAsync(Req(HttpMethod.Post, $"/api/admin/content/{hidden}/unpublish", t), ct)).EnsureSuccessStatusCode();

        fixture.YouTubeStub.Videos["v1"] = fixture.YouTubeStub.Videos["v1"] with { Views = 12345 };
        await SyncAsync(client, t, ct);

        (await ContentAsync(client, t, ct)).Single(i => i.GetProperty("slug").GetString() == "v1")
            .GetProperty("status").GetString().Should().Be("Draft");
    }

    [DockerFact]
    public async Task A_playlist_is_shown_instead_of_the_uploads()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        Channel(uploads: 5);
        fixture.YouTubeStub.Playlists["PLfavouritesfavourites"] = ["v4", "v3"];
        fixture.YouTubeStub.PlaylistTitles["PLfavouritesfavourites"] = "Favourites";
        var t = await InstanceAsync(client, new
        {
            channel = "@acme",
            playlist = "https://www.youtube.com/playlist?list=PLfavouritesfavourites",
        }, ct);

        await SyncAsync(client, t, ct);

        (await SlugsAsync(client, t, ct)).Should().BeEquivalentTo(["v4", "v3"]);
    }

    [DockerFact]
    public async Task A_link_to_no_channel_is_reported_and_the_resolve_route_previews_a_good_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        Channel(uploads: 1);
        var t = await InstanceAsync(client, new { channel = "@nobody-here" }, ct);

        var res = await client.SendAsync(Req(HttpMethod.Post, $"/api/admin/plugins/videos/sync", t), ct);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(ct)).Should().Contain("No YouTube channel matches");

        var preview = await client.SendAsync(Req(HttpMethod.Get, $"/api/admin/plugins/videos/resolve?channel=%40acme", t), ct);
        preview.EnsureSuccessStatusCode();
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("channel").GetProperty("id").GetString().Should().Be(ChannelId);
        body.GetProperty("channel").GetProperty("title").GetString().Should().Be("Acme Bakery");
    }

    [DockerFact]
    public async Task The_timer_skips_an_unchanged_feed_and_only_a_new_video_is_news()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        Channel(uploads: 4);
        var t = await InstanceAsync(client, new { channel = "@acme", maxVideos = 3 }, ct);
        await SyncAsync(client, t, ct);
        (await OutboxAsync(t, ct)).Should().OnlyContain(e => e.Quiet, "the first import is not news");

        // Only the counters moved: the timer pass reads one page and stops there.
        fixture.YouTubeStub.Videos["v0"] = fixture.YouTubeStub.Videos["v0"] with { Views = 999 };
        var videosRead = fixture.YouTubeStub.RequestsFor("/youtube/v3/videos");
        (await TimerPassAsync(t, ct)).Created.Should().Be(0);
        fixture.YouTubeStub.RequestsFor("/youtube/v3/videos").Should().Be(videosRead);
        (await ItemDataAsync(t, "v0", ct)).GetProperty("viewCount").GetInt64().Should().Be(10);

        // A new upload: synced, announced on its own; the refreshes and the trimmed oldest are quiet.
        fixture.YouTubeStub.Videos["new"] = new YouTubeStubServer.Video("new", Newest.AddDays(1));
        fixture.YouTubeStub.Playlists[Uploads].Insert(0, "new");
        var before = (await OutboxAsync(t, ct)).Count;
        var pass = await TimerPassAsync(t, ct);

        pass.Created.Should().Be(1);
        pass.Removed.Should().Be(1);
        var events = (await OutboxAsync(t, ct)).Skip(before).ToList();
        events.Where(e => !e.Quiet).Select(e => (e.Subject, e.Slug)).Should().Equal((Subjects.ContentPublished, "new"));
        (await ItemDataAsync(t, "v0", ct)).GetProperty("viewCount").GetInt64().Should().Be(999);
    }

    [DockerFact]
    public async Task With_auto_publish_off_new_videos_wait_as_drafts()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        Channel(uploads: 2);
        var t = await InstanceAsync(client, new { channel = "@acme", maxVideos = 3, autoPublish = false }, ct);

        await SyncAsync(client, t, ct);

        var row = await ItemRowAsync(t, "v0", ct);
        row.Status.Should().Be(ContentStatus.Draft);
        row.PublishedVersionId.Should().BeNull();
        row.CurrentDraftVersionId.Should().NotBeNull();
        (await OutboxAsync(t, ct)).Should().BeEmpty();
    }

    [DockerFact]
    public async Task A_cap_above_the_ceiling_is_refused_by_the_server()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        var tenant = await TenantAsync(client, ct);

        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", tenant, new
        {
            pluginId = YouTubePlugin.PluginId,
            slug = "videos",
            name = "Videos",
            config = JsonSerializer.Serialize(new { channel = "@acme", maxVideos = YouTubeSettings.MaxVideosCeiling + 1 }),
        }), ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(ct)).Should().Contain(YouTubeSettings.MaxVideosKey);
    }

    // ---------- helpers ----------

    private sealed record OutboxEvent(string Subject, string Slug, bool Quiet);

    /// <summary>The content events the instance's syncs wrote, oldest first.</summary>
    private async Task<List<OutboxEvent>> OutboxAsync(Tenant t, CancellationToken ct)
    {
        var tenantId = (await ItemRowAsync(t, "v0", ct)).TenantId;
        using var scope = fixture.Factory.Services.CreateScope();
        var cms = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        using var rls = RlsScope.Platform();
        var rows = await cms.Outbox.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.TenantId == tenantId)
            .OrderBy(o => o.OccurredAt)
            .Select(o => new { o.Subject, o.PayloadJson })
            .ToListAsync(ct);
        return rows.Select(r =>
        {
            var e = JsonDocument.Parse(r.PayloadJson).RootElement;
            return new OutboxEvent(r.Subject, e.GetProperty("Slug").GetString()!, e.GetProperty("Quiet").GetBoolean());
        }).ToList();
    }

    /// <summary>What the 30-minute job does for the instance (the job itself is off in tests).</summary>
    private async Task<YouTubeSyncOutcome> TimerPassAsync(Tenant t, CancellationToken ct)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var tenantId = (await ItemRowAsync(t, "v0", ct)).TenantId;
        var instance = (await scope.ServiceProvider.GetRequiredService<PluginContextFactory>()
            .EnabledInstancesAsync(tenantId, ct)).Single(i => i.InstanceId == t.InstanceId);
        var outcome = await scope.ServiceProvider.GetRequiredService<YouTubeSync>().SyncAsync(instance, ct, onlyWhenChanged: true);
        outcome.Error.Should().BeNull();
        return outcome;
    }

    private sealed record Tenant(Guid Owner, string Slug, Guid InstanceId = default);

    private HttpClient Client() => fixture.Factory.CreateClient();

    /// <summary>
    /// The channel @acme with <paramref name="uploads"/> videos v0…vN, newest first, a day apart.
    /// </summary>
    private void Channel(int uploads, string[]? shorts = null, string[]? live = null)
    {
        var stub = fixture.YouTubeStub;
        stub.Reset();
        stub.Handles["acme"] = (ChannelId, "Acme Bakery");
        stub.Playlists[Uploads] = Enumerable.Range(0, uploads).Select(i => $"v{i}").ToList();
        if (shorts is not null) stub.Playlists[Shorts] = [.. shorts];
        for (var i = 0; i < uploads; i++)
        {
            var id = $"v{i}";
            stub.Videos[id] = new YouTubeStubServer.Video(id, Newest.AddDays(-i), Live: live?.Contains(id) == true);
        }
    }

    private async Task<Tenant> TenantAsync(HttpClient client, CancellationToken ct)
    {
        var owner = Guid.NewGuid();
        var slug = "yt-" + Guid.NewGuid().ToString("N")[..8];
        var provision = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", new Tenant(owner, ""),
            new { slug, name = slug, ownerUserId = owner, ownerEmail = "o@dcms.test" }, asSuperAdmin: true), ct);
        provision.EnsureSuccessStatusCode();
        return new Tenant(owner, slug);
    }

    private async Task<Tenant> InstanceAsync(HttpClient client, object config, CancellationToken ct)
    {
        var tenant = await TenantAsync(client, ct);
        var create = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", tenant, new
        {
            pluginId = YouTubePlugin.PluginId,
            slug = "videos",
            name = "Videos",
            config = JsonSerializer.Serialize(config),
        }), ct);
        create.IsSuccessStatusCode.Should().BeTrue(await create.Content.ReadAsStringAsync(ct));
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        return tenant with { InstanceId = id };
    }

    private static async Task<JsonElement> SyncAsync(HttpClient client, Tenant t, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/videos/sync", t), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, "body was: {0}", await res.Content.ReadAsStringAsync(ct));
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static async Task<List<JsonElement>> ContentAsync(HttpClient client, Tenant t, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Get,
            $"/api/admin/content?instanceId={t.InstanceId}&contentType={YouTubePlugin.VideoType}", t), ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items").EnumerateArray().ToList();
    }

    private static async Task<List<string?>> SlugsAsync(HttpClient client, Tenant t, CancellationToken ct) =>
        (await ContentAsync(client, t, ct)).Select(i => i.GetProperty("slug").GetString()).ToList();

    private async Task<JsonElement> ItemDataAsync(Tenant t, string slug, CancellationToken ct)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var cms = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        using var rls = RlsScope.Platform();
        var json = await cms.ContentItems.IgnoreQueryFilters()
            .Where(c => c.Slug == slug && c.PluginInstanceId == t.InstanceId)
            .Join(cms.ContentVersions.IgnoreQueryFilters(), c => c.PublishedVersionId, v => v.Id, (_, v) => v.DataJson)
            .FirstAsync(ct);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private async Task<ContentItem> ItemRowAsync(Tenant t, string slug, CancellationToken ct)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var cms = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        using var rls = RlsScope.Platform();
        return await cms.ContentItems.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.Slug == slug && c.PluginInstanceId == t.InstanceId, ct);
    }

    private async Task WithItemAsync(Tenant t, string slug, Action<ContentItem> change, CancellationToken ct)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var cms = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        using var rls = RlsScope.Platform();
        change(await cms.ContentItems.IgnoreQueryFilters().SingleAsync(c => c.Slug == slug && c.PluginInstanceId == t.InstanceId, ct));
        await cms.SaveChangesAsync(ct);
    }

    private async Task<int> VersionCountAsync(Tenant t, string slug, CancellationToken ct)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var cms = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        using var rls = RlsScope.Platform();
        return await cms.ContentItems.IgnoreQueryFilters()
            .Where(c => c.Slug == slug && c.PluginInstanceId == t.InstanceId)
            .SelectMany(c => c.Versions)
            .CountAsync(ct);
    }

    private static HttpRequestMessage Req(
        HttpMethod method, string url, Tenant t, object? body = null, bool asSuperAdmin = false)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", t.Owner.ToString());
        req.Headers.Add("X-Test-Email", $"{t.Owner:N}@dcms.test");
        if (asSuperAdmin) req.Headers.Add("X-Test-Roles", "SuperAdmin");
        if (t.Slug.Length > 0) req.Headers.Add("X-Dcms-Tenant", t.Slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }
}
