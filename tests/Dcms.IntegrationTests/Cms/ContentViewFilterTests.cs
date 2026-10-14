using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Dcms.IntegrationTests.Cms;

/// <summary>
/// #278: a saved view filters a collection on any of its fields — the type's own and the
/// tenant's custom ones — in AND/OR groups, sorts by one of them and shows them as columns.
/// A roster: text fields of its own, and number, boolean, date and tags fields the tenant added.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public class ContentViewFilterTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    [DockerFact]
    public async Task A_view_filters_sorts_and_shows_any_field_of_the_collection()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await RosterAsync(ct);
        await CreateAsync(t, "ann", new { name = "Ann", role = "DJ", custom = new { age = 31, active = true, joined = "2024-03-01", skills = new[] { "vinyl", "techno" } } }, ct);
        await CreateAsync(t, "bob", new { name = "Bob", role = "VJ", custom = new { age = "25", active = false, joined = "2025-01-10", skills = new[] { "video" } } }, ct);
        await CreateAsync(t, "cyd", new { name = "Cyd", role = "Sound engineer", custom = new { } }, ct);
        await CreateAsync(t, "dee", new { name = "Dee", role = "DJ", custom = new { age = 45, joined = "2023-07-15", skills = new[] { "techno" } } }, ct);

        (await SlugsAsync(t, new { field = "role", op = "eq", value = "DJ" }, ct)).Should().BeEquivalentTo("ann", "dee");
        (await SlugsAsync(t, new
        {
            or = new object[]
            {
                new { field = "custom.age", op = "gte", value = 40 },
                new { and = new object[] { new { field = "custom.active", op = "eq", value = true }, new { field = "custom.skills", op = "contains", value = "vinyl" } } },
            },
        }, ct)).Should().BeEquivalentTo("ann", "dee");
        // A box never ticked is false; a number stored as text is still a number.
        (await SlugsAsync(t, new { not = new { field = "custom.active", op = "eq", value = true } }, ct)).Should().BeEquivalentTo("bob", "cyd", "dee");
        (await SlugsAsync(t, new { field = "custom.age", op = "lt", value = 30 }, ct)).Should().BeEquivalentTo("bob");
        (await SlugsAsync(t, new { field = "custom.age", op = "isNull", value = true }, ct)).Should().BeEquivalentTo("cyd");
        (await SlugsAsync(t, new { field = "custom.joined", op = "lt", value = "2024-12-31" }, ct)).Should().BeEquivalentTo("ann", "dee");
        (await SlugsAsync(t, new { field = "custom.skills", op = "in", value = new[] { "video", "vinyl" } }, ct)).Should().BeEquivalentTo("ann", "bob");
        (await SlugsAsync(t, new { field = "custom.skills", op = "nin", value = new[] { "video", "vinyl" } }, ct)).Should().BeEquivalentTo("cyd", "dee");
        (await SlugsAsync(t, new { field = "role", op = "contains", value = "ENG" }, ct)).Should().BeEquivalentTo("cyd");
        (await SlugsAsync(t, new { field = "_slug", op = "startsWith", value = "d" }, ct)).Should().BeEquivalentTo("dee");
        // A wildcard typed into a condition is a character.
        (await SlugsAsync(t, new { field = "role", op = "contains", value = "%" }, ct)).Should().BeEmpty();

        // A field the collection does not have, or an operator its type does not take: refused.
        (await StatusAsync(t, Filter(new { field = "nope", op = "eq", value = "x" }), ct)).Should().Be(HttpStatusCode.BadRequest);
        (await StatusAsync(t, Filter(new { field = "custom.skills", op = "gt", value = "a" }), ct)).Should().Be(HttpStatusCode.BadRequest);
        (await StatusAsync(t, Filter(new { field = "role", op = "like", value = "x" }), ct)).Should().Be(HttpStatusCode.BadRequest);

        // Sorted by a custom number, empties last, across pages; with the view's columns.
        var walked = new List<string>();
        string? cursor = null;
        do
        {
            var page = await PageAsync(t, "sort=field:custom.age&dir=asc&limit=2&columns=role,custom.age" +
                (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}"), ct);
            foreach (var item in page.GetProperty("items").EnumerateArray())
            {
                walked.Add(item.GetProperty("slug").GetString()!);
                if (item.GetProperty("slug").GetString() == "ann")
                {
                    item.GetProperty("values").GetProperty("role").GetString().Should().Be("DJ");
                    item.GetProperty("values").GetProperty("custom.age").GetInt32().Should().Be(31);
                }
            }
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null && walked.Count < 10);
        walked.Should().Equal("bob", "ann", "dee", "cyd");
    }

    private static string Filter(object filter) => "filter=" + Uri.EscapeDataString(JsonSerializer.Serialize(filter));

    private sealed record Roster(string Slug, Guid Owner, Guid InstanceId);

    private async Task<Roster> RosterAsync(CancellationToken ct)
    {
        var admin = fixture.Admin.CreateClient();
        var slug = "view-" + Guid.NewGuid().ToString("N")[..8];
        var owner = Guid.NewGuid();
        (await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "", "SuperAdmin",
            new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct)).EnsureSuccessStatusCode();
        var config = JsonSerializer.Serialize(new
        {
            fields = new object[]
            {
                new { key = "age", label = "Age", type = "number" },
                new { key = "active", label = "Active", type = "boolean" },
                new { key = "joined", label = "Joined", type = "date" },
                new { key = "skills", label = "Skills", type = "tags" },
            },
        });
        var instance = await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, slug,
            body: new { pluginId = "roster", slug = "crew", name = "Crew", config }), ct);
        instance.StatusCode.Should().Be(HttpStatusCode.Created, await instance.Content.ReadAsStringAsync(ct));
        return new Roster(slug, owner, (await instance.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid());
    }

    private async Task CreateAsync(Roster t, string itemSlug, object data, CancellationToken ct)
    {
        var res = await fixture.Admin.CreateClient().SendAsync(Req(HttpMethod.Post, "/api/admin/content", t.Owner, t.Slug,
            body: new { pluginInstanceId = t.InstanceId, contentType = "member", slug = itemSlug, data }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync(ct));
    }

    private async Task<List<string>> SlugsAsync(Roster t, object filter, CancellationToken ct) =>
        [.. (await PageAsync(t, Filter(filter), ct)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("slug").GetString()!)];

    private async Task<JsonElement> PageAsync(Roster t, string query, CancellationToken ct)
    {
        var res = await fixture.Admin.CreateClient().SendAsync(Req(HttpMethod.Get,
            $"/api/admin/content/page?instanceId={t.InstanceId}&contentType=member&{query}", t.Owner, t.Slug), ct);
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(ct));
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task<HttpStatusCode> StatusAsync(Roster t, string query, CancellationToken ct) =>
        (await fixture.Admin.CreateClient().SendAsync(Req(HttpMethod.Get,
            $"/api/admin/content/page?instanceId={t.InstanceId}&contentType=member&{query}", t.Owner, t.Slug), ct)).StatusCode;

    private static HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string slug, string roles = "", object? body = null)
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
