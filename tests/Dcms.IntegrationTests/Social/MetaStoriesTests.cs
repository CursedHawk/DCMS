using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.Plugins.Instagram;
using Dcms.Plugins.Meta.Core;

namespace Dcms.IntegrationTests.Social;

/// <summary>
/// The live stories path: content-api → admin-api → Meta, with Redis in front.
///
/// <para>Three separate claims are worth holding, and each fails in its own way. The route has
/// to reach the <i>live</i> handler rather than the generic content one, or stories are always
/// an empty list from a table nothing writes to. The cache has to hold, or every page view on
/// a busy site becomes a Meta call and the app's rate budget is gone by lunchtime. And the
/// service token has to be confined to this endpoint, or granting content-api — the
/// internet-facing service — an admin-api audience has quietly opened the admin plane.</para>
/// </summary>
[Collection(ContentFlowCollection.Name)]
public class MetaStoriesTests(ContentFlowFixture fixture)
{
    [DockerFact]
    public async Task Stories_are_served_live_from_meta_and_then_from_the_cache()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await ConnectedInstanceAsync(showStories: true, ct);

        fixture.MetaStub.Stories.Add(fixture.MetaStub.Post("s1", "Today", DateTimeOffset.UtcNow));
        fixture.MetaStub.Stories.Add(fixture.MetaStub.Post("s2", "Also today", DateTimeOffset.UtcNow));

        var first = await GetStoriesAsync(t, ct);

        first.GetProperty("items").GetArrayLength().Should().Be(2);
        first.GetProperty("totalCount").GetInt32().Should().Be(2);
        fixture.MetaStub.RequestsFor("/stories").Should().Be(1);

        var story = first.GetProperty("items")[0];
        story.GetProperty("contentType").GetString().Should().Be(InstagramPlugin.StoryType);
        story.GetProperty("slug").GetString().Should().Be("s1");
        // A story is gone in 24 hours, so there is nothing worth mirroring — the CDN link is
        // the honest answer here, and the field vocabulary is otherwise a synced post's.
        story.GetProperty("data").GetProperty("mediaUrl").GetString().Should().NotBeNullOrEmpty();
        story.GetProperty("data").GetProperty("media").ValueKind.Should().Be(JsonValueKind.Null);

        // Meta is not asked again. Without this the story strip alone would spend the app's
        // whole rate budget on a site with any traffic at all.
        var second = await GetStoriesAsync(t, ct);
        second.GetProperty("items").GetArrayLength().Should().Be(2);
        fixture.MetaStub.RequestsFor("/stories").Should().Be(1, "the second read must come from Redis");
    }

    [DockerFact]
    public async Task The_literal_story_route_wins_over_the_generic_content_route()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await ConnectedInstanceAsync(showStories: true, ct);

        fixture.MetaStub.Stories.Add(fixture.MetaStub.Post("s9", "Live", DateTimeOffset.UtcNow));

        var res = await GetStoriesAsync(t, ct);

        // The decisive evidence that DeliveryEndpoints' /api/{slug}/{contentType} did not
        // answer: it would have returned an empty list (nothing ever writes story rows), and
        // it never calls Meta. One request to the stub says the live handler ran.
        res.GetProperty("items").GetArrayLength().Should().Be(1);
        fixture.MetaStub.RequestsFor("/stories").Should().Be(1);

        // And the plugin does not declare the route either, so if that precedence ever changed
        // the generic handler would 404 rather than silently serve nothing.
        var generic = await fixture.Content.CreateClient().SendAsync(
            TenantReq(t.Slug, $"/api/{t.InstanceSlug}/{InstagramPlugin.StoryType}/s9"), ct);
        generic.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task Stories_switched_off_never_reach_meta()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await ConnectedInstanceAsync(showStories: false, ct);

        fixture.MetaStub.Stories.Add(fixture.MetaStub.Post("s3", "Hidden", DateTimeOffset.UtcNow));

        var res = await GetStoriesAsync(t, ct);

        res.GetProperty("items").GetArrayLength().Should().Be(0);
        fixture.MetaStub.RequestsFor("/stories").Should().Be(
            0, "an instance with stories off must not spend a Meta call to discover that");
    }

    [DockerFact]
    public async Task A_service_token_with_the_wrong_scope_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await ConnectedInstanceAsync(showStories: true, ct);
        fixture.MetaStub.Stories.Add(fixture.MetaStub.Post("s4", "Live", DateTimeOffset.UtcNow));

        fixture.ServiceScope = "dcms.ai";
        try
        {
            var res = await GetStoriesAsync(t, ct);

            // content-api degrades to an empty strip rather than failing the page — but the
            // point is what happened upstream: admin-api refused, so Meta was never called.
            res.GetProperty("items").GetArrayLength().Should().Be(0);
            fixture.MetaStub.RequestsFor("/stories").Should().Be(0);
        }
        finally
        {
            fixture.ServiceScope = "dcms.social";
        }
    }

    [DockerFact]
    public async Task A_service_token_cannot_reach_the_rest_of_the_admin_plane()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await ConnectedInstanceAsync(showStories: true, ct);

        // A bare RequireAuthorization() endpoint — the class of route that asks only for an
        // authenticated caller. Before ServicePrincipalGuard, a client-credentials token
        // satisfied that, and granting content-api an admin-api audience for one story read
        // would have handed the public service every one of them.
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/plugins/instances");
        req.Headers.Add("X-Test-Sub", "dcms-admin-api");
        req.Headers.Add("X-Test-Scope", "dcms.social");
        req.Headers.Add("X-Dcms-Tenant", t.Slug);

        var res = await fixture.Admin.CreateClient().SendAsync(req, ct);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---------- helpers ----------

    private sealed record Tenant(Guid Owner, string Slug, string InstanceSlug);

    private static readonly Guid SuperAdmin = Guid.NewGuid();

    private async Task<Tenant> ConnectedInstanceAsync(bool showStories, CancellationToken ct)
    {
        var admin = fixture.Admin.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = Guid.NewGuid();
        var slug = "story-" + Guid.NewGuid().ToString("N")[..8];
        var instanceSlug = "gram-" + Guid.NewGuid().ToString("N")[..8];

        (await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, null, "SuperAdmin",
            new { slug, name = slug, ownerUserId = owner, ownerEmail = "o@dcms.test" }), ct))
            .EnsureSuccessStatusCode();

        fixture.MetaStub.Reset();
        fixture.MetaStub.Pages.Add(
            MetaStubServer.Page("page-1", "Acme Bakery", "PAGE-TOKEN", "ig-1", "acmebakery"));

        var connect = await admin.SendAsync(
            AdminReq(HttpMethod.Get, "/api/admin/social/facebook/connect", owner, slug), ct);
        connect.EnsureSuccessStatusCode();
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(
            (await connect.Content.ReadFromJsonAsync<JsonElement>(ct))
            .GetProperty("authorizeUrl").GetString()!).Query)["state"]!;

        (await admin.GetAsync($"/api/admin/social/callback?code=auth-code&state={state}", ct))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        var connections = (await (await admin.SendAsync(
                AdminReq(HttpMethod.Get, "/api/admin/social/connections", owner, slug), ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().ToList();

        var connectionId = connections
            .Single(c => c.GetProperty("accountUsername").GetString() == "acmebakery")
            .GetProperty("id").GetGuid();

        var config = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [MetaFeedConfig.ConnectionIdKey] = connectionId.ToString(),
            // Nothing is synced in these tests; the caps only need to be valid.
            [MetaFeedConfig.MaxPostsKey] = 0,
            [MetaFeedConfig.MaxReelsKey] = 0,
            [MetaFeedConfig.ShowStoriesKey] = showStories,
        });

        (await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/plugins/instances", owner, slug,
            body: new { pluginId = InstagramPlugin.PluginId, slug = instanceSlug, name = "Feed", config }), ct))
            .EnsureSuccessStatusCode();

        // The connect flow itself talks to the stub; only story calls should be counted.
        fixture.MetaStub.Requests.Clear();

        return new Tenant(owner, slug, instanceSlug);
    }

    private async Task<JsonElement> GetStoriesAsync(Tenant t, CancellationToken ct)
    {
        var res = await fixture.Content.CreateClient().SendAsync(
            TenantReq(t.Slug, $"/api/{t.InstanceSlug}/{InstagramPlugin.StoryType}"), ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, "body was: {0}", await res.Content.ReadAsStringAsync(ct));
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static HttpRequestMessage TenantReq(string slug, string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Dcms-Tenant", slug);
        return req;
    }

    private static HttpRequestMessage AdminReq(
        HttpMethod method, string url, Guid sub, string? slug, string? roles = null, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (roles is not null) req.Headers.Add("X-Test-Roles", roles);
        if (slug is not null) req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }
}
