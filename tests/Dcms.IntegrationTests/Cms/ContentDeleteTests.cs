using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.DynamicApps;

namespace Dcms.IntegrationTests.Cms;

/// <summary>
/// DELETE /api/admin/content/{id}: the console's bulk delete called it before it existed. An item
/// goes with its versions and schedule; a published one is unpublishing too, so it needs that permission.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class ContentDeleteTests(ContentFlowFixture fixture)
{
    [DockerFact]
    public async Task Deleting_takes_versions_and_schedule_along_and_a_live_item_needs_publish()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.InstallAsync("blog", "news", "{}", ct);
        var instances = await (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/plugins/instances", app.Owner, app.Tenant), ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);
        var news = instances.EnumerateArray().Single(i => i.GetProperty("slug").GetString() == "news").GetProperty("id").GetGuid();

        async Task<Guid> PostAsync(string slug)
        {
            var res = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/content", app.Owner, app.Tenant,
                body: new { pluginInstanceId = news, contentType = "post", slug, data = new { title = slug } }), ct);
            res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync(ct));
            return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        }
        Task<HttpResponseMessage> Send(HttpMethod method, string path, Guid? @as = null, object? body = null) =>
            app.Admin.SendAsync(AppHarness.Req(method, path, @as ?? app.Owner, app.Tenant, body: body), ct);

        var live = await PostAsync("live");
        (await Send(HttpMethod.Post, $"/api/admin/content/{live}/publish", body: new { })).EnsureSuccessStatusCode();
        (await Send(HttpMethod.Post, $"/api/admin/content/{live}/schedule", body: new { publishAt = DateTimeOffset.UtcNow.AddDays(1) }))
            .EnsureSuccessStatusCode();
        var draft = await PostAsync("draft");

        // A writer who may not publish deletes drafts, not what is on the site.
        var writer = await app.AddMemberAsync(["content:read", "content:write"], ct);
        (await Send(HttpMethod.Delete, $"/api/admin/content/{live}", writer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Delete, $"/api/admin/content/{draft}", writer)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Send(HttpMethod.Delete, $"/api/admin/content/{live}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Send(HttpMethod.Get, $"/api/admin/content/{live}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Delete, $"/api/admin/content/{live}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
