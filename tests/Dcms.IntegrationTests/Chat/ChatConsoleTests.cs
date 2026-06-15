using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Npgsql;

namespace Dcms.IntegrationTests.Chat;

/// <summary>
/// Exercises the chat schema + agent-console endpoint end to end against a real
/// Postgres, and asserts cross-tenant isolation: a conversation seeded under
/// tenant A is listed for A but never leaks to tenant B (the ChatDbContext query
/// filter, the same backstop RLS reinforces).
/// </summary>
[Collection(AdminApiCollection.Name)]
public class ChatConsoleTests(AdminApiFixture fixture)
{
    private static HttpRequestMessage Get(string url, string tenantSlug)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Test-Sub", Guid.NewGuid().ToString());
        req.Headers.Add("X-Test-Roles", "SuperAdmin"); // bypasses tenant permission checks
        req.Headers.Add("X-Dcms-Tenant", tenantSlug);
        return req;
    }

    [DockerFact]
    public async Task Agent_console_lists_only_the_current_tenants_conversations()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var (tenantA, slugA) = await CreateTenantAsync(client, ct);
        var (_, slugB) = await CreateTenantAsync(client, ct);

        var conversationId = Guid.NewGuid();
        await SeedConversationAsync(tenantA, conversationId, "Ada", ct);

        // Tenant A sees its conversation.
        var aRes = await client.SendAsync(Get("/api/admin/chat/conversations", slugA), ct);
        aRes.EnsureSuccessStatusCode();
        var aList = await aRes.Content.ReadFromJsonAsync<JsonElement>(ct);
        aList.EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).Should().Contain(conversationId);

        // Tenant B does not.
        var bRes = await client.SendAsync(Get("/api/admin/chat/conversations", slugB), ct);
        bRes.EnsureSuccessStatusCode();
        var bList = await bRes.Content.ReadFromJsonAsync<JsonElement>(ct);
        bList.EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).Should().NotContain(conversationId);
    }

    private static async Task<(Guid Id, string Slug)> CreateTenantAsync(HttpClient client, CancellationToken ct)
    {
        var slug = "chat-" + Guid.NewGuid().ToString("N")[..8];
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Headers = { { "X-Test-Sub", Guid.NewGuid().ToString() }, { "X-Test-Roles", "SuperAdmin" } },
            Content = JsonContent.Create(new { slug, name = slug, ownerUserId = Guid.NewGuid(), ownerEmail = $"{slug}@dcms.test" }),
        };
        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return (json.GetProperty("tenantId").GetGuid(), slug);
    }

    private async Task SeedConversationAsync(Guid tenantId, Guid conversationId, string visitorName, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chat.conversations
                ("Id", "TenantId", "VisitorId", "VisitorName", "Status", "CreatedAt", "LastMessageAt")
            VALUES (@id, @tenant, NULL, @name, 0, now(), now())
            """;
        cmd.Parameters.AddWithValue("id", conversationId);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("name", visitorName);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
