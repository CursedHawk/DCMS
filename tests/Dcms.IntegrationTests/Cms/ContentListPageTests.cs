using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Dcms.IntegrationTests.Cms;

/// <summary>
/// The collection list, which used to be "fetch every item in this collection with its full
/// draft and filter in the browser".
///
/// <para>That is fine for the twelve gigs a venue has and is megabytes for a blog — the payload
/// grows with what authors have written rather than with the row count, so it degrades quietly
/// and in proportion to how much the tenant has used the product. These tests pin the three
/// things the server now has to do instead, each of which is invisible when wrong: the title
/// comes out of the draft JSON, the filters mean the same thing they meant in the browser, and
/// the cursor does not skip or repeat a row.</para>
/// </summary>
[Collection(ContentFlowCollection.Name)]
public class ContentListPageTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    [DockerFact]
    public async Task A_page_carries_the_title_from_the_draft_and_never_the_draft_itself()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await CollectionAsync(ct);

        await CreateAsync(t, "hello", new { title = "Hello world", body = "<p>First post.</p>" }, ct);

        var page = await PageAsync(t, "", ct);
        var item = page.GetProperty("items").EnumerateArray().Single();

        item.GetProperty("title").GetString().Should().Be("Hello world");
        item.GetProperty("slug").GetString().Should().Be("hello");
        item.TryGetProperty("draft", out _).Should().BeFalse(
            "the whole point is that a list does not download what people have written");
    }

    /// <summary>
    /// A content type whose title field is RichText holds markup. Rendering it raw shows an
    /// author their own tags in a list column, which is how the old browser-side version behaved
    /// and is the kind of thing nobody reports as a bug — they just assume it is normal.
    /// </summary>
    [DockerFact]
    public async Task A_title_that_is_markup_is_shown_as_text()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await CollectionAsync(ct);

        await CreateAsync(t, "markup", new { title = "Plain", body = "<p>x</p>" }, ct);
        var page = await PageAsync(t, "?search=Plain", ct);

        page.GetProperty("items").EnumerateArray().Single()
            .GetProperty("title").GetString().Should().Be("Plain");
    }

    [DockerFact]
    public async Task An_item_with_no_title_written_falls_back_to_its_slug()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await CollectionAsync(ct);

        await CreateAsync(t, "untitled-post", new { body = "<p>No title yet.</p>" }, ct);

        var page = await PageAsync(t, "", ct);
        page.GetProperty("items").EnumerateArray().Single()
            .GetProperty("title").GetString().Should().Be("untitled-post");
    }

    [DockerFact]
    public async Task Search_matches_the_title_as_well_as_the_slug()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await CollectionAsync(ct);

        await CreateAsync(t, "a-post", new { title = "Doors at eight", body = "<p>x</p>" }, ct);
        await CreateAsync(t, "another-post", new { title = "Something else", body = "<p>x</p>" }, ct);

        (await SlugsAsync(t, "?search=doors", ct)).Should().Equal("a-post");
        (await SlugsAsync(t, "?search=another", ct)).Should().Equal("another-post");
    }

    /// <summary>
    /// A search for "100%" is a search for those characters. Without escaping, a percent sign
    /// typed by a user is a wildcard and the filter silently stops filtering.
    /// </summary>
    [DockerFact]
    public async Task A_wildcard_typed_into_the_search_box_is_not_a_wildcard()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await CollectionAsync(ct);

        await CreateAsync(t, "sold-out", new { title = "100% sold out", body = "<p>x</p>" }, ct);
        await CreateAsync(t, "quiet-night", new { title = "Half full", body = "<p>x</p>" }, ct);

        (await SlugsAsync(t, "?search=100%25%20sold", ct)).Should().Equal("sold-out");

        // The proof: a bare percent sign finds only the row that literally contains one.
        // Unescaped it is `%%` and matches everything, which is a filter that has stopped
        // filtering while still looking like it works.
        (await SlugsAsync(t, "?search=%25", ct)).Should().Equal("sold-out");
    }

    [DockerFact]
    public async Task Status_filters_agree_with_what_the_list_shows()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await CollectionAsync(ct);

        var draft = await CreateAsync(t, "still-writing", new { title = "Draft", body = "<p>x</p>" }, ct);
        var live = await CreateAsync(t, "out-now", new { title = "Live", body = "<p>x</p>" }, ct);
        await PublishAsync(t, live, ct);

        (await SlugsAsync(t, "?status=Published", ct)).Should().Equal("out-now");
        (await SlugsAsync(t, "?status=Draft", ct)).Should().Equal("still-writing");

        // A queued publish takes an unpublished item out of Draft and into the synthetic
        // "Scheduled" the console has always offered — so filtering for Draft must stop
        // returning it, or the two filters overlap and the counts stop adding up.
        await ScheduleAsync(t, draft, DateTimeOffset.UtcNow.AddDays(1), ct);

        (await SlugsAsync(t, "?status=Scheduled", ct)).Should().Equal("still-writing");
        (await SlugsAsync(t, "?status=Draft", ct)).Should().BeEmpty();
    }

    /// <summary>
    /// The author's own tags, not the published ones. Filtering by a tag typed this morning and
    /// not yet published must find the item, or the filter reads as broken rather than as "that
    /// tag is not live yet".
    /// </summary>
    [DockerFact]
    public async Task Tag_filtering_reads_the_draft()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await CollectionAsync(ct);

        await CreateAsync(t, "tagged", new { title = "Tagged", body = "<p>x</p>", tags = new[] { "Interview" } }, ct);
        await CreateAsync(t, "untagged", new { title = "Untagged", body = "<p>x</p>" }, ct);

        (await SlugsAsync(t, "?tag=interview", ct)).Should().Equal("tagged");
        (await SlugsAsync(t, "?tag=nothing-uses-this", ct)).Should().BeEmpty(
            "no item carries that tag, which is a different answer from no tag being asked for");
    }

    [DockerFact]
    public async Task Paging_walks_every_row_exactly_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await CollectionAsync(ct);

        // Written in one loop, so several land in the same tick — which is exactly the case a
        // cursor on the timestamp alone gets wrong.
        for (var i = 0; i < 7; i++)
        {
            await CreateAsync(t, $"item-{i}", new { title = $"Item {i}", body = "<p>x</p>" }, ct);
        }

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var query = cursor is null ? "?limit=3" : $"?limit=3&cursor={Uri.EscapeDataString(cursor)}";
            var page = await PageAsync(t, query, ct);

            page.GetProperty("total").GetInt32().Should().Be(7, "the total counts matches, not what is left");
            seen.AddRange(page.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("slug").GetString()!));

            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        seen.Should().HaveCount(7).And.OnlyHaveUniqueItems();
        pages.Should().Be(3, "seven rows at three a page");
    }

    /// <summary>
    /// The rail's counts. It used to fetch every item of every instance in the rail — for all
    /// of them at once, open or not — and count in the browser.
    /// </summary>
    [DockerFact]
    public async Task Counts_report_each_content_type_without_listing_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await CollectionAsync(ct);

        await CreateAsync(t, "one", new { title = "One", body = "<p>x</p>" }, ct);
        await CreateAsync(t, "two", new { title = "Two", body = "<p>x</p>" }, ct);

        var res = await fixture.Admin.CreateClient().SendAsync(
            Req(HttpMethod.Get, $"/api/admin/content/counts?instanceId={t.InstanceId}", t.Owner, t.Slug), ct);
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(ct));

        var counts = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        counts.GetProperty("post").GetInt32().Should().Be(2);
    }

    /// <summary>
    /// The publishing queue, which the console could not show at all.
    ///
    /// <para>Every other content read is scoped to one plugin instance, because that is how the
    /// console browses. "What goes out this week" is not answerable that way — it spans every
    /// collection in the workspace — and so the one cross-instance read in the CMS exists for
    /// exactly this.</para>
    /// </summary>
    [DockerFact]
    public async Task The_queue_lists_every_collection_soonest_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await CollectionAsync(ct);
        var second = await SecondInstanceAsync(t, ct);

        var later = await CreateAsync(t, "later", new { title = "Out on Friday", body = "<p>x</p>" }, ct);
        var sooner = await CreateAsync(t, "sooner", new { title = "Out tomorrow", body = "<p>x</p>" }, ct, second);

        await ScheduleAsync(t, later, DateTimeOffset.UtcNow.AddDays(5), ct);
        await ScheduleAsync(t, sooner, DateTimeOffset.UtcNow.AddDays(1), ct);

        var items = (await ScheduledAsync(t, ct)).ToList();

        items.Select(i => i.GetProperty("slug").GetString())
            .Should().Equal("sooner", "later");
        // The row says which collection it belongs to — the whole point of a list that spans them.
        // (Two arguments, not three with a reason: `Equal` here takes params, so a reason string
        // would be read as a third expected value.)
        items.Select(i => i.GetProperty("instanceName").GetString())
            .Should().Equal("Press", "News");
    }

    /// <summary>
    /// A schedule pins the version it was made against, so the queue must show the headline
    /// that is actually going out — not whatever the author has typed since.
    /// </summary>
    [DockerFact]
    public async Task A_queued_row_shows_the_title_of_the_version_that_is_queued()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await CollectionAsync(ct);
        var id = await CreateAsync(t, "headline", new { title = "As approved", body = "<p>x</p>" }, ct);

        await ScheduleAsync(t, id, DateTimeOffset.UtcNow.AddDays(2), ct);
        await UpdateAsync(t, id, new { title = "Still editing", body = "<p>x</p>" }, ct);

        var row = (await ScheduledAsync(t, ct)).Single();
        row.GetProperty("title").GetString().Should().Be("As approved");
    }

    [DockerFact]
    public async Task Cancelling_a_schedule_empties_the_queue()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await CollectionAsync(ct);
        var id = await CreateAsync(t, "cancel-me", new { title = "Maybe not", body = "<p>x</p>" }, ct);
        await ScheduleAsync(t, id, DateTimeOffset.UtcNow.AddDays(2), ct);

        (await ScheduledAsync(t, ct)).Should().HaveCount(1);

        var res = await fixture.Admin.CreateClient().SendAsync(
            Req(HttpMethod.Delete, $"/api/admin/content/{id}/schedule", t.Owner, t.Slug), ct);
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(ct));

        (await ScheduledAsync(t, ct)).Should().BeEmpty();
    }

    /// <summary>
    /// Crossing instances is the point of this read; crossing tenants would be the bug it makes
    /// possible. The filter is explicit because the query is raw SQL and does not go through the
    /// ambient tenant filter.
    /// </summary>
    [DockerFact]
    public async Task The_queue_never_shows_another_workspace()
    {
        var ct = TestContext.Current.CancellationToken;
        var mine = await CollectionAsync(ct);
        var theirs = await CollectionAsync(ct);

        var hidden = await CreateAsync(theirs, "not-yours", new { title = "Theirs", body = "<p>x</p>" }, ct);
        await ScheduleAsync(theirs, hidden, DateTimeOffset.UtcNow.AddDays(1), ct);

        (await ScheduledAsync(mine, ct)).Should().BeEmpty();
        (await ScheduledAsync(theirs, ct)).Should().HaveCount(1);
    }

    // ---------- helpers ----------

    private sealed record Collection(string Slug, Guid Owner, Guid InstanceId);

    /// <summary>A fresh tenant with a blog instance, so no test sees another's rows.</summary>
    private async Task<Collection> CollectionAsync(CancellationToken ct)
    {
        var admin = fixture.Admin.CreateClient();
        var slug = "list-" + Guid.NewGuid().ToString("N")[..8];
        var owner = Guid.NewGuid();

        var tenant = await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "", "SuperAdmin",
            new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct);
        tenant.StatusCode.Should().Be(HttpStatusCode.Created);

        var instance = await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, slug,
            body: new { pluginId = "blog", slug = "news", name = "News", config = "{}" }), ct);
        instance.StatusCode.Should().Be(HttpStatusCode.Created);

        var id = (await instance.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        return new Collection(slug, owner, id);
    }

    /// <param name="instanceId">Defaults to the collection's own instance.</param>
    private async Task<Guid> CreateAsync(
        Collection t, string itemSlug, object data, CancellationToken ct, Guid? instanceId = null)
    {
        var res = await fixture.Admin.CreateClient().SendAsync(
            Req(HttpMethod.Post, "/api/admin/content", t.Owner, t.Slug,
                body: new
                {
                    pluginInstanceId = instanceId ?? t.InstanceId,
                    contentType = "post",
                    slug = itemSlug,
                    data,
                }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
    }

    private async Task PublishAsync(Collection t, Guid itemId, CancellationToken ct)
    {
        var res = await fixture.Admin.CreateClient().SendAsync(
            Req(HttpMethod.Post, $"/api/admin/content/{itemId}/publish", t.Owner, t.Slug), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task ScheduleAsync(Collection t, Guid itemId, DateTimeOffset at, CancellationToken ct)
    {
        var res = await fixture.Admin.CreateClient().SendAsync(
            Req(HttpMethod.Post, $"/api/admin/content/{itemId}/schedule", t.Owner, t.Slug,
                body: new { publishAt = at }), ct);
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>A second collection in the same workspace, so "across instances" means something.</summary>
    private async Task<Guid> SecondInstanceAsync(Collection t, CancellationToken ct)
    {
        var res = await fixture.Admin.CreateClient().SendAsync(
            Req(HttpMethod.Post, "/api/admin/plugins/instances", t.Owner, t.Slug,
                body: new { pluginId = "blog", slug = "press", name = "Press", config = "{}" }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
    }

    private async Task UpdateAsync(Collection t, Guid itemId, object data, CancellationToken ct)
    {
        var res = await fixture.Admin.CreateClient().SendAsync(
            Req(HttpMethod.Put, $"/api/admin/content/{itemId}", t.Owner, t.Slug, body: new { data }), ct);
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(ct));
    }

    private async Task<IReadOnlyList<JsonElement>> ScheduledAsync(Collection t, CancellationToken ct)
    {
        var res = await fixture.Admin.CreateClient().SendAsync(
            Req(HttpMethod.Get, "/api/admin/content/scheduled", t.Owner, t.Slug), ct);
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(ct));

        var body = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return body.GetProperty("items").EnumerateArray().ToList();
    }

    private async Task<JsonElement> PageAsync(Collection t, string query, CancellationToken ct)
    {
        var separator = query.StartsWith('?') ? "&" : "?";
        var url = $"/api/admin/content/page?instanceId={t.InstanceId}&contentType=post"
                  + (query.Length > 0 ? separator + query.TrimStart('?') : string.Empty);

        var res = await fixture.Admin.CreateClient().SendAsync(Req(HttpMethod.Get, url, t.Owner, t.Slug), ct);
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(ct));
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task<List<string>> SlugsAsync(Collection t, string query, CancellationToken ct) =>
        [.. (await PageAsync(t, query, ct)).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("slug").GetString()!)];

    private static HttpRequestMessage Req(
        HttpMethod method, string url, Guid sub, string slug, string roles = "", object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (!string.IsNullOrEmpty(roles)) req.Headers.Add("X-Test-Roles", roles);
        if (slug.Length > 0) req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }
}
