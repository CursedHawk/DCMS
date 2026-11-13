extern alias AdminApiApp;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdminApiApp::Dcms.AdminApi.Ai;
using Dcms.IntegrationTests.Tenancy;

namespace Dcms.IntegrationTests.Ai;

/// <summary>
/// A tenant's AI agent policy: the site agents' turn limit (100 unless the platform console says
/// otherwise) and its overrides of the system prompts — set only through the console, read by the
/// tenant's own agents.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class AiAgentPolicyTests(AdminApiFixture fixture)
{
    [DockerFact]
    public async Task A_new_tenant_s_agents_get_100_turns_and_the_shipped_prompts()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (_, owner, slug) = await TenantAsync(client, ct);

        var policy = await PolicyAsync(client, owner, slug, ct);
        policy.GetProperty("maxTurns").GetInt32().Should().Be(100);
        policy.GetProperty("prompts").EnumerateObject().Should().BeEmpty();
    }

    [DockerFact]
    public async Task The_console_sets_it_and_the_tenant_s_agents_read_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (tenantId, owner, slug) = await TenantAsync(client, ct);

        var set = await SetAsync(client, tenantId, new
        {
            maxTurns = 40,
            prompts = new Dictionary<string, string> { ["site.react"] = "You build {{site}}.", ["assistant"] = "  " },
        }, ct);
        set.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var policy = await PolicyAsync(client, owner, slug, ct);
        policy.GetProperty("maxTurns").GetInt32().Should().Be(40);
        // A blank override is no override: the shipped prompt applies.
        policy.GetProperty("prompts").EnumerateObject().Select(p => (p.Name, p.Value.GetString()))
            .Should().Equal(("site.react", "You build {{site}}."));

        // The tenant's own AI settings write leaves the platform's policy alone.
        (await client.SendAsync(Req(HttpMethod.Put, "/api/admin/ai/settings", owner, slug,
            new { provider = "Anthropic", model = "x" }), ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await PolicyAsync(client, owner, slug, ct)).GetProperty("maxTurns").GetInt32().Should().Be(40);

        var console = await client.SendAsync(Req(HttpMethod.Get, $"/api/admin/tenants/{tenantId}/ai-agent-policy",
            Guid.NewGuid(), null, asSuperAdmin: true), ct);
        var body = await console.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("maxTurns").GetInt32().Should().Be(40);
        body.GetProperty("defaultMaxTurns").GetInt32().Should().Be(100);

        // Back to the defaults.
        (await SetAsync(client, tenantId, new { maxTurns = (int?)null, prompts = new Dictionary<string, string>() }, ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await PolicyAsync(client, owner, slug, ct)).GetProperty("maxTurns").GetInt32().Should().Be(100);
    }

    [DockerFact]
    public async Task Only_the_console_sets_it_and_only_within_bounds()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (tenantId, owner, slug) = await TenantAsync(client, ct);

        // The tenant's owner holds every tenant permission, and still may not change it.
        (await client.SendAsync(Req(HttpMethod.Put, $"/api/admin/tenants/{tenantId}/ai-agent-policy", owner, slug,
            new { maxTurns = 500 }), ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await SetAsync(client, tenantId, new { maxTurns = 0 }, ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SetAsync(client, tenantId, new { maxTurns = 501 }, ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SetAsync(client, tenantId, new { prompts = new Dictionary<string, string> { ["chatbot"] = "x" } }, ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SetAsync(client, Guid.NewGuid(), new { maxTurns = 10 }, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static Task<HttpResponseMessage> SetAsync(HttpClient client, Guid tenantId, object body, CancellationToken ct) =>
        client.SendAsync(Req(HttpMethod.Put, $"/api/admin/tenants/{tenantId}/ai-agent-policy", Guid.NewGuid(), null,
            body, asSuperAdmin: true), ct);

    private static async Task<JsonElement> PolicyAsync(HttpClient client, Guid owner, string slug, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/ai/agent-policy", owner, slug), ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static async Task<(Guid TenantId, Guid Owner, string Slug)> TenantAsync(HttpClient client, CancellationToken ct)
    {
        var owner = Guid.NewGuid();
        var slug = "aipol-" + Guid.NewGuid().ToString("N")[..8];
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", owner, null,
            new { slug, name = slug, ownerUserId = owner, ownerEmail = "o@dcms.test" }, asSuperAdmin: true), ct);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return (body.GetProperty("tenantId").GetGuid(), owner, slug);
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

/// <summary>
/// No container: a source scan, outside the admin-api collection so it runs (and is not skipped)
/// without Docker.
/// </summary>
public partial class AiPromptCatalogueTests
{
    /// <summary>The server's keys are the catalogue's: a prompt it refuses is one the console offers.</summary>
    [Fact]
    public void The_prompt_keys_match_the_catalogue()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dcms.sln"))) directory = directory.Parent;
        Assert.SkipWhen(directory is null, "Repository root not found; the source tree is not available here.");

        var catalogue = File.ReadAllText(Path.Combine(directory!.FullName, "packages/core/src/aiPrompts.ts"));
        var keys = CatalogueKey().Matches(catalogue).Select(m => m.Groups[1].Value).ToList();

        keys.Should().NotBeEmpty();
        keys.Should().BeEquivalentTo(AiAgentPolicyEndpoints.PromptKeys);
    }

    [GeneratedRegex(@"^\s*key: '([\w.]+)',", RegexOptions.Multiline)]
    private static partial Regex CatalogueKey();
}
