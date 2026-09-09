using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Npgsql;

namespace Dcms.IntegrationTests.Ai;

/// <summary>
/// Who may read whose assistant conversation.
///
/// <para>These transcripts hold whole tool results — draft content, analytics figures — lifted
/// out of a tenant's data, so the boundaries are not a nicety. Three of them are tested here:
/// another tenant never sees a row; a colleague sees a conversation only once its owner has
/// shared it; and reading everyone's is a permission, not something an owner simply is.</para>
///
/// <para>Deliberately run as ordinary members rather than SuperAdmins — a SuperAdmin bypasses
/// every check in this file, so a suite written with one would pass with the permission check
/// deleted.</para>
/// </summary>
[Collection(AdminApiCollection.Name)]
public class AiConversationTests(AdminApiFixture fixture)
{
    private static readonly Guid Provisioner = Guid.NewGuid();

    [DockerFact]
    public async Task A_conversation_is_invisible_to_another_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (ownerA, slugA, _) = await NewTenantAsync(client, ct);
        var (ownerB, slugB, _) = await NewTenantAsync(client, ct);

        var id = await CreateConversationAsync(client, ownerA, slugA, "tenant A's work", ct);

        // Even shared with "the workspace", the workspace means A's.
        await ShareAsync(client, ownerA, slugA, id, ct);

        var listed = await ListAsync(client, ownerB, slugB, "workspace", ct);
        listed.Should().NotContain(id);

        var direct = await client.SendAsync(Get($"/api/admin/ai/conversations/{id}", ownerB, slugB), ct);
        direct.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task A_private_conversation_is_invisible_to_a_colleague()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (owner, slug, tenantId) = await NewTenantAsync(client, ct);
        var colleague = await AddMemberAsync(client, owner, slug, tenantId, ct);

        var id = await CreateConversationAsync(client, owner, slug, "private thinking", ct);

        (await ListAsync(client, colleague, slug, "mine", ct)).Should().NotContain(id);
        (await ListAsync(client, colleague, slug, "workspace", ct)).Should().NotContain(id);

        // Reported as absent rather than refused: "you may not read this" still tells them it
        // exists and who else is talking to the assistant.
        var direct = await client.SendAsync(Get($"/api/admin/ai/conversations/{id}", colleague, slug), ct);
        direct.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task Sharing_a_conversation_makes_it_readable_by_the_workspace()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (owner, slug, tenantId) = await NewTenantAsync(client, ct);
        var colleague = await AddMemberAsync(client, owner, slug, tenantId, ct);

        var id = await CreateConversationAsync(client, owner, slug, "how we ship", ct);
        await ShareAsync(client, owner, slug, id, ct);

        (await ListAsync(client, colleague, slug, "workspace", ct)).Should().Contain(id);

        var direct = await client.SendAsync(Get($"/api/admin/ai/conversations/{id}", colleague, slug), ct);
        direct.EnsureSuccessStatusCode();
        var json = await direct.Content.ReadFromJsonAsync<JsonElement>(ct);
        // It is readable, and it says plainly that continuing it is not theirs to do.
        json.GetProperty("mine").GetBoolean().Should().BeFalse();
    }

    [DockerFact]
    public async Task Reading_everyone_s_conversations_needs_the_permission()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (owner, slug, tenantId) = await NewTenantAsync(client, ct);
        var colleague = await AddMemberAsync(client, owner, slug, tenantId, ct);

        var id = await CreateConversationAsync(client, owner, slug, "the owner's own", ct);

        // A member holding only site:edit — enough to use the assistant, not to audit it.
        var refused = await client.SendAsync(Get("/api/admin/ai/conversations?scope=all", colleague, slug), ct);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The Owner role is seeded with every platform permission, this one included.
        (await ListAsync(client, owner, slug, "all", ct)).Should().Contain(id);
    }

    [DockerFact]
    public async Task Only_the_owner_may_continue_rename_or_delete_a_shared_conversation()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (owner, slug, tenantId) = await NewTenantAsync(client, ct);
        var colleague = await AddMemberAsync(client, owner, slug, tenantId, ct);

        var id = await CreateConversationAsync(client, owner, slug, "shared work", ct);
        await ShareAsync(client, owner, slug, id, ct);

        // Readable, but not writable: the tools would run with the reader's permissions
        // against work they did not do.
        var append = Req(HttpMethod.Post, $"/api/admin/ai/conversations/{id}/messages", colleague, slug,
            new { messages = new[] { new { role = "user", content = new[] { new { type = "text", text = "hi" } } } } });
        (await client.SendAsync(append, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var rename = Req(HttpMethod.Patch, $"/api/admin/ai/conversations/{id}", colleague, slug, new { title = "mine now" });
        (await client.SendAsync(rename, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var delete = Req(HttpMethod.Delete, $"/api/admin/ai/conversations/{id}", colleague, slug);
        (await client.SendAsync(delete, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task Turns_come_back_as_the_blocks_they_were_stored_as()
    {
        // The whole persistence design rests on this: the stored content is handed straight
        // back to the model as history, so a turn that came back as a quoted string, or with
        // its tool_use block flattened, would break resuming rather than merely look wrong.
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (owner, slug, _) = await NewTenantAsync(client, ct);
        var id = await CreateConversationAsync(client, owner, slug, "draft me a post", ct);

        var append = Req(HttpMethod.Post, $"/api/admin/ai/conversations/{id}/messages", owner, slug, new
        {
            messages = new object[]
            {
                new { role = "user", content = new object[] { new { type = "text", text = "draft me a post" } } },
                new
                {
                    role = "assistant",
                    content = new object[]
                    {
                        new { type = "text", text = "On it." },
                        new
                        {
                            type = "tool_use",
                            id = "call-1",
                            name = "create_content",
                            input = new { slug = "autumn-26" },
                        },
                    },
                },
            },
        });
        (await client.SendAsync(append, ct)).EnsureSuccessStatusCode();

        var res = await client.SendAsync(Get($"/api/admin/ai/conversations/{id}", owner, slug), ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);

        var messages = json.GetProperty("messages").EnumerateArray().ToList();
        messages.Should().HaveCount(2);
        messages[0].GetProperty("role").GetString().Should().Be("user");

        var blocks = messages[1].GetProperty("content");
        blocks.ValueKind.Should().Be(JsonValueKind.Array, "content is JSON, not a quoted string");
        blocks[1].GetProperty("name").GetString().Should().Be("create_content");
        blocks[1].GetProperty("input").GetProperty("slug").GetString().Should().Be("autumn-26");

        json.GetProperty("messageCount").GetInt32().Should().Be(2);
    }

    [DockerFact]
    public async Task Full_auto_is_never_remembered_on_a_conversation()
    {
        // Publishing and deleting with nobody watching is a decision for the person sitting
        // there, not a posture a fortnight-old conversation restores on being reopened.
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (owner, slug, _) = await NewTenantAsync(client, ct);

        var create = Req(HttpMethod.Post, "/api/admin/ai/conversations", owner, slug,
            new { title = "unattended", mode = "auto" });
        var created = await client.SendAsync(create, ct);
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        var res = await client.SendAsync(Get($"/api/admin/ai/conversations/{id}", owner, slug), ct);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        json.GetProperty("mode").GetString().Should().Be("agent");

        var patch = Req(HttpMethod.Patch, $"/api/admin/ai/conversations/{id}", owner, slug, new { mode = "auto" });
        (await client.SendAsync(patch, ct)).EnsureSuccessStatusCode();

        var after = await client.SendAsync(Get($"/api/admin/ai/conversations/{id}", owner, slug), ct);
        (await after.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("mode").GetString()
            .Should().Be("agent");
    }

    [DockerFact]
    public async Task Deleting_a_conversation_takes_its_messages_with_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (owner, slug, _) = await NewTenantAsync(client, ct);
        var id = await CreateConversationAsync(client, owner, slug, "throwaway", ct);

        var append = Req(HttpMethod.Post, $"/api/admin/ai/conversations/{id}/messages", owner, slug,
            new { messages = new[] { new { role = "user", content = new[] { new { type = "text", text = "hi" } } } } });
        (await client.SendAsync(append, ct)).EnsureSuccessStatusCode();

        var delete = Req(HttpMethod.Delete, $"/api/admin/ai/conversations/{id}", owner, slug);
        (await client.SendAsync(delete, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT count(*) FROM ai.messages WHERE "ConversationId" = @id""";
        cmd.Parameters.AddWithValue("id", id);
        var remaining = (long)(await cmd.ExecuteScalarAsync(ct))!;
        remaining.Should().Be(0, "the cascade is the database's, not EF's change tracker's");
    }

    // ---- helpers ----------------------------------------------------------

    private static HttpRequestMessage Req(
        HttpMethod method, string url, Guid sub, string? tenantSlug = null, object? body = null,
        string roles = "")
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (!string.IsNullOrEmpty(roles)) req.Headers.Add("X-Test-Roles", roles);
        if (tenantSlug is not null) req.Headers.Add("X-Dcms-Tenant", tenantSlug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static HttpRequestMessage Get(string url, Guid sub, string tenantSlug) =>
        Req(HttpMethod.Get, url, sub, tenantSlug);

    private static async Task<(Guid Owner, string Slug, Guid TenantId)> NewTenantAsync(HttpClient client, CancellationToken ct)
    {
        var owner = Guid.NewGuid();
        var slug = "aic-" + Guid.NewGuid().ToString("N")[..8];
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", Provisioner,
            body: new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" },
            roles: "SuperAdmin"), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return (owner, slug, json.GetProperty("tenantId").GetGuid());
    }

    /// <summary>
    /// A second member of the tenant holding only <c>site:edit</c> — enough to use the
    /// assistant, not enough to read anybody else's conversation. Seeded directly because
    /// members join through invitations, and an invitation round trip would test the invitation
    /// flow rather than this one.
    /// </summary>
    private async Task<Guid> AddMemberAsync(
        HttpClient client, Guid owner, string slug, Guid tenantId, CancellationToken ct)
    {
        var roleRes = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/roles", owner, slug,
            new { name = "Assistant user " + Guid.NewGuid().ToString("N")[..6], permissions = new[] { "site:edit" } }), ct);
        roleRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var roleId = (await roleRes.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        var member = Guid.NewGuid();
        var membershipId = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenancy.tenant_memberships ("Id", "TenantId", "UserId", "Email", "CreatedAt")
            VALUES (@mid, @tid, @uid, @email, now());
            INSERT INTO tenancy.member_roles ("Id", "TenantId", "MembershipId", "TenantRoleId")
            VALUES (@mrid, @tid, @mid, @rid);
            """;
        cmd.Parameters.AddWithValue("mid", membershipId);
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("uid", member);
        cmd.Parameters.AddWithValue("email", $"{member:N}@dcms.test");
        cmd.Parameters.AddWithValue("mrid", Guid.NewGuid());
        cmd.Parameters.AddWithValue("rid", roleId);
        await cmd.ExecuteNonQueryAsync(ct);

        return member;
    }

    private static async Task<Guid> CreateConversationAsync(
        HttpClient client, Guid sub, string slug, string title, CancellationToken ct)
    {
        var res = await client.SendAsync(
            Req(HttpMethod.Post, "/api/admin/ai/conversations", sub, slug, new { title, mode = "agent" }), ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
    }

    private static async Task ShareAsync(
        HttpClient client, Guid sub, string slug, Guid id, CancellationToken ct)
    {
        var res = await client.SendAsync(
            Req(HttpMethod.Patch, $"/api/admin/ai/conversations/{id}", sub, slug, new { visibility = "Workspace" }), ct);
        res.EnsureSuccessStatusCode();
    }

    private static async Task<List<Guid>> ListAsync(
        HttpClient client, Guid sub, string slug, string scope, CancellationToken ct)
    {
        var res = await client.SendAsync(Get($"/api/admin/ai/conversations?scope={scope}", sub, slug), ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();
    }
}
