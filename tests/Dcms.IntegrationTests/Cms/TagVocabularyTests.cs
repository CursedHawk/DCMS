using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Dcms.IntegrationTests.Cms;

/// <summary>
/// The workspace's tag vocabulary — what every tag input in the admin suggests — comes from
/// fields declared as tags and nothing else. It used to scan every array in every item, so an
/// event's line-up (a Json array of performers) turned its guests into tags offered everywhere,
/// short entries arriving as raw JSON objects.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public class TagVocabularyTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    [DockerFact]
    public async Task Only_tag_fields_feed_the_vocabulary_not_a_line_up_or_other_arrays()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = fixture.Admin.CreateClient();
        var slug = "tags-" + Guid.NewGuid().ToString("N")[..8];
        var owner = Guid.NewGuid();

        (await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "", "SuperAdmin",
            new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var gigs = await InstanceAsync(admin, owner, slug, "events", "gigs", "{}", ct);
        // A tenant's own fields: "skills" is declared a tag field, "notes" is not.
        var crew = await InstanceAsync(admin, owner, slug, "roster", "crew", JsonSerializer.Serialize(new
        {
            fields = new object[]
            {
                new { key = "skills", label = "Skills", type = "tags" },
                new { key = "notes", label = "Notes", type = "text" },
            },
        }), ct);

        await CreateAsync(admin, owner, slug, gigs, "gig", "opening", new
        {
            title = "Opening",
            date = "2026-11-01T20:00:00Z",
            venue = "Moordoor",
            genres = new[] { "techno" },
            // Short enough to have slipped under the old length cap as a JSON "tag".
            performers = new object[] { new { performerName = "Kofe", visible = true } },
        }, ct);
        await CreateAsync(admin, owner, slug, gigs, "gig", "closing", new
        {
            title = "Closing",
            date = "2026-11-02T20:00:00Z",
            venue = "Moordoor",
            // A line-up written as plain names is still a line-up, not tags.
            performers = new[] { "Guest Star" },
        }, ct);
        await CreateAsync(admin, owner, slug, crew, "member", "ana", new
        {
            name = "Ana",
            custom = new { skills = new[] { "drums" }, notes = new[] { "backstage" } },
        }, ct);

        var res = await admin.SendAsync(Req(HttpMethod.Get, "/api/admin/content/tags", owner, slug), ct);
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(ct));
        var tags = (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray()
            .Select(t => t.GetProperty("tag").GetString()).ToList();

        tags.Should().BeEquivalentTo(["techno", "drums"]);
    }

    private static async Task<Guid> InstanceAsync(
        HttpClient admin, Guid owner, string tenant, string pluginId, string slug, string config, CancellationToken ct)
    {
        var res = await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, tenant,
            body: new { pluginId, slug, name = slug, config }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync(ct));
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
    }

    private static async Task CreateAsync(
        HttpClient admin, Guid owner, string tenant, Guid instanceId, string type, string slug, object data, CancellationToken ct)
    {
        var res = await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/content", owner, tenant,
            body: new { pluginInstanceId = instanceId, contentType = type, slug, data }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync(ct));
    }

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
