using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Ai;

/// <summary>A workspace's several AI providers, the picker's choices, and its usage page.</summary>
[Collection(AdminApiCollection.Name)]
public class AiProvidersTests(AdminApiFixture fixture)
{
    [DockerFact]
    public async Task A_workspace_keeps_several_providers_with_exactly_one_default()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (_, owner, slug) = await TenantAsync(client, ct);

        var first = await CreateAsync(client, owner, slug, new { name = "Anthropic", provider = "Anthropic", apiKey = "sk-a" }, ct);
        var second = await CreateAsync(client, owner, slug, new { name = "Local", provider = "OpenAi", model = "llama" }, ct);
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/ai/providers", owner, slug,
            new { name = "Local", provider = "OpenAi" }), ct)).StatusCode.Should().Be(HttpStatusCode.Conflict, "names are unique");

        var listed = await ListAsync(client, owner, slug, ct);
        listed.Select(p => (p.GetProperty("name").GetString(), p.GetProperty("isDefault").GetBoolean(), p.GetProperty("hasApiKey").GetBoolean()))
            .Should().Equal(("Anthropic", true, true), ("Local", false, false));
        listed[0].TryGetProperty("apiKeyCiphertext", out _).Should().BeFalse("a key never leaves the server");

        // Making the second the default unsets the first.
        (await client.SendAsync(Req(HttpMethod.Put, $"/api/admin/ai/providers/{second}", owner, slug,
            new { name = "Local", provider = "OpenAi", model = "llama", isDefault = true }), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ListAsync(client, owner, slug, ct)).Select(p => (p.GetProperty("name").GetString(), p.GetProperty("isDefault").GetBoolean()))
            .Should().Equal(("Local", true), ("Anthropic", false));

        // A conversation pinned to a provider goes back to the default when the provider is deleted.
        var conversation = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/ai/conversations", owner, slug,
            new { title = "t", providerId = first }), ct);
        var conversationId = (await conversation.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        (await DetailAsync(client, owner, slug, conversationId, ct)).GetProperty("providerId").GetGuid().Should().Be(first);

        (await client.SendAsync(Req(HttpMethod.Delete, $"/api/admin/ai/providers/{second}", owner, slug), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ListAsync(client, owner, slug, ct)).Single().GetProperty("isDefault").GetBoolean()
            .Should().BeTrue("deleting the default promotes the one left");
        (await client.SendAsync(Req(HttpMethod.Delete, $"/api/admin/ai/providers/{first}", owner, slug), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await DetailAsync(client, owner, slug, conversationId, ct)).GetProperty("providerId").ValueKind
            .Should().Be(JsonValueKind.Null);

        // Another workspace's provider cannot be pinned.
        var (_, otherOwner, otherSlug) = await TenantAsync(client, ct);
        var foreign = await CreateAsync(client, otherOwner, otherSlug, new { name = "Theirs", provider = "Anthropic" }, ct);
        var pinned = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/ai/conversations", owner, slug,
            new { title = "t", providerId = foreign }), ct);
        var pinnedId = (await pinned.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        (await DetailAsync(client, owner, slug, pinnedId, ct)).GetProperty("providerId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [DockerFact]
    public async Task The_picker_and_usage_page_show_providers_and_the_callers_own_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (tenantId, owner, slug) = await TenantAsync(client, ct);
        var main = await CreateAsync(client, owner, slug, new { name = "Main", provider = "Anthropic", apiKey = "sk-a" }, ct);

        var choices = await GetAsync(client, owner, slug, "/api/admin/ai/provider-choices", ct);
        choices.GetProperty("providers").EnumerateArray().Single().GetProperty("name").GetString().Should().Be("Main");
        choices.GetProperty("own").ValueKind.Should().Be(JsonValueKind.Null, "no personal settings yet");

        (await client.SendAsync(Req(HttpMethod.Put, "/api/admin/ai/user-credentials", owner, slug,
            new { provider = "Anthropic", apiKey = "sk-mine" }), ct)).EnsureSuccessStatusCode();
        (await GetAsync(client, owner, slug, "/api/admin/ai/provider-choices", ct)).GetProperty("own")
            .GetProperty("hasApiKey").GetBoolean().Should().BeTrue();

        var someoneElse = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        using (var scope = fixture.Factory.Services.CreateScope())
        using (RlsScope.Tenant(tenantId))
        {
            var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
            db.Usage.AddRange(
                Usage(tenantId, today, AiUsageSources.Workspace, main, null, 1000),
                Usage(tenantId, today.AddDays(-40), AiUsageSources.Workspace, main, null, 7),
                Usage(tenantId, today, AiUsageSources.User, null, owner, 50),
                Usage(tenantId, today, AiUsageSources.User, null, someoneElse, 999));
            await db.SaveChangesAsync(ct);
        }

        var usage = await GetAsync(client, owner, slug, "/api/admin/ai/usage?days=30", ct);
        usage.GetProperty("workspace").EnumerateArray().Select(r => r.GetProperty("inputTokens").GetInt64())
            .Should().Equal([1000L], "the range is 30 days, and members' own keys are not the workspace's");
        usage.GetProperty("mine").EnumerateArray().Select(r => r.GetProperty("inputTokens").GetInt64())
            .Should().Equal([50L], "only the caller's own key, never another member's");
        usage.GetProperty("own").GetProperty("provider").GetString().Should().Be("Anthropic");
    }

    private static AiUsageDay Usage(Guid tenant, DateOnly day, string source, Guid? provider, Guid? user, long input) => new()
    {
        TenantId = tenant, Day = day, Source = source, ProviderId = provider, UserId = user,
        ProviderName = "x", ProviderKind = "anthropic", Model = "m", Calls = 1, InputTokens = input,
    };

    private static async Task<Guid> CreateAsync(HttpClient client, Guid owner, string slug, object body, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/ai/providers", owner, slug, body), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(ct));
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, Guid owner, string slug, CancellationToken ct) =>
        (await GetAsync(client, owner, slug, "/api/admin/ai/providers", ct)).EnumerateArray().ToList();

    private static Task<JsonElement> DetailAsync(HttpClient client, Guid owner, string slug, Guid id, CancellationToken ct) =>
        GetAsync(client, owner, slug, $"/api/admin/ai/conversations/{id}", ct);

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid owner, string slug, string url, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Get, url, owner, slug), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(ct));
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static async Task<(Guid TenantId, Guid Owner, string Slug)> TenantAsync(HttpClient client, CancellationToken ct)
    {
        var owner = Guid.NewGuid();
        var slug = "aiprov-" + Guid.NewGuid().ToString("N")[..8];
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
