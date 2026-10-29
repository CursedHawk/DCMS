using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Dcms.IntegrationTests.Cms;

/// <summary>
/// An app link (ADR 0026, 0028): a development environment's site reads production's published content
/// through its own <c>/api/{slug}</c>, read only; links stay inside one workspace.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class EnvironmentLinkTests(ContentFlowFixture fixture)
{
    private static HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string? slug = null, object? body = null, bool superAdmin = false)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (superAdmin) req.Headers.Add("X-Test-Roles", "SuperAdmin");
        if (slug is not null) Environments.EnvironmentTarget.Apply(req, slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static HttpRequestMessage Site(HttpMethod method, string slug, string url)
    {
        var req = new HttpRequestMessage(method, url);
        Environments.EnvironmentTarget.Apply(req, slug);
        return req;
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage res, CancellationToken ct)
    {
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(ct));
        return res.Content.Headers.ContentLength == 0 ? default : await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    [DockerFact]
    public async Task Development_reads_productions_published_content_through_a_link()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = fixture.Admin.CreateClient();
        var content = fixture.Content.CreateClient();
        var owner = Guid.NewGuid();
        var prod = "link-" + Guid.NewGuid().ToString("N")[..8];
        await Ok(await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", owner, body:
            new { slug = prod, name = prod, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }, superAdmin: true), ct), ct);
        var news = (await Ok(await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, prod,
            new { pluginId = "blog", slug = "news", name = "News", config = "{}" }), ct), ct)).GetProperty("id").GetGuid();
        var item = (await Ok(await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/content", owner, prod,
            new { pluginInstanceId = news, contentType = "post", slug = "hello", data = new { title = "From production" } }), ct), ct)).GetProperty("id").GetGuid();
        await Ok(await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/content/{item}/publish", owner, prod), ct), ct);

        var dev = (await Ok(await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, prod, new { name = "development", empty = true }), ct), ct))
            .GetProperty("id").GetGuid();
        var prodId = (await Ok(await admin.SendAsync(Req(HttpMethod.Get, "/api/admin/environments", owner, prod), ct), ct)).GetProperty("workspaceId").GetGuid();
        var devSlug = $"{prod}/development";

        // Without a link, development has no such app.
        (await content.SendAsync(Site(HttpMethod.Get, devSlug, "/api/news/post/hello"), ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A link names an app the target has, in the same workspace.
        (await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/environments/{dev}/links", owner, prod,
            new { kind = "app", targetEnvironmentId = prodId, targetKey = "nope", access = "read" }), ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/environments/{dev}/links", owner, prod,
            new { kind = "app", targetEnvironmentId = Guid.NewGuid(), targetKey = "news", access = "read" }), ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await Ok(await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/environments/{dev}/links", owner, prod,
            new { kind = "app", targetEnvironmentId = prodId, targetKey = "news", access = "read" }), ct), ct);

        var served = await Ok(await content.SendAsync(Site(HttpMethod.Get, devSlug, "/api/news/post/hello"), ct), ct);
        served.GetProperty("data").GetProperty("title").GetString().Should().Be("From production");

        // Read only: a write through the link is refused, not sent to production.
        (await content.SendAsync(Site(HttpMethod.Post, devSlug, "/api/news/post"), ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A duplicate is all or nothing: a source holding something the copy refuses (here an app of
    /// its own and an app link at one address, from before that was refused) leaves no half-made
    /// environment behind, and the job says what stopped it.
    /// </summary>
    [DockerFact]
    public async Task A_copy_that_stops_partway_is_removed_again_and_says_on_what()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = fixture.Admin.CreateClient();
        var owner = Guid.NewGuid();
        var prod = "copy-" + Guid.NewGuid().ToString("N")[..8];
        await Ok(await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", owner, body:
            new { slug = prod, name = prod, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }, superAdmin: true), ct), ct);
        await Ok(await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, prod,
            new { pluginId = "blog", slug = "news", name = "News", config = "{}" }), ct), ct);
        var billing = (await Ok(await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, prod,
            new { name = "billing", service = true, empty = true }), ct), ct)).GetProperty("id").GetGuid();
        await Ok(await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, $"{prod}/billing",
            new { pluginId = "blog", slug = "news", name = "News", config = "{}" }), ct), ct);
        var prodId = (await Ok(await admin.SendAsync(Req(HttpMethod.Get, "/api/admin/environments", owner, prod), ct), ct)).GetProperty("workspaceId").GetGuid();
        await using (var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $$"""
                INSERT INTO tenancy.environment_links ("Id","TenantId","EnvironmentId","Kind","TargetTenantId","TargetKey","Access","CreatedAt")
                VALUES ('{{Guid.NewGuid()}}','{{prodId}}','{{prodId}}','app','{{billing}}','news','Read',now());
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var qa = (await Ok(await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, prod, new { name = "qa" }), ct), ct))
            .GetProperty("id").GetGuid();
        JsonElement job = default;
        for (var i = 0; i < 200; i++)
        {
            var jobs = await Ok(await admin.SendAsync(Req(HttpMethod.Get, "/api/admin/jobs?pageSize=20", owner, prod), ct), ct);
            job = jobs.GetProperty("items").EnumerateArray().FirstOrDefault(j => j.GetProperty("kind").GetString() == "environments.copy");
            if (job.ValueKind == JsonValueKind.Object && job.GetProperty("status").GetString() is "Failed" or "Succeeded") break;
            await Task.Delay(250, ct);
        }
        job.GetProperty("status").GetString().Should().Be("Failed");
        job.GetProperty("errorKey").GetString().Should().Be("environments.errors.copyRemoved");
        job.GetProperty("errorDetail").GetString().Should().Contain("links 'app/billing/news'").And.Contain("its own app at this address");

        var left = await Ok(await admin.SendAsync(Req(HttpMethod.Get, "/api/admin/environments", owner, prod), ct), ct);
        left.GetProperty("environments").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().NotContain(qa);
        left.GetProperty("environments").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "production")
            .GetProperty("promotesFrom").EnumerateArray().Should().BeEmpty("the pipeline is as it was before the copy");
    }
}
