extern alias AdminApiApp;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime.Hosting;
using Dcms.Shared.Contracts.Events;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Npgsql;

namespace Dcms.IntegrationTests.Sites;

/// <summary>
/// A plugin's repository (ADR 0029) end to end against a real Forgejo: <c>dcms.repos@1</c>
/// creates it for a Functions instance, the IDE's repo routes edit, commit and merge it under the
/// binding's own permissions, and a push to it reaches plugins as <c>dcms.repo.pushed</c>.
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class PluginRepoTests(AdminApiFixture fixture) : IAsyncLifetime
{
    private const string Secret = "plugin-repo-webhook-secret";

    private TestForgejo? _forgejo;

    public async ValueTask InitializeAsync()
    {
        if (DockerFactAttribute.Available)
        {
            _forgejo = await TestForgejo.StartAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_forgejo is not null)
        {
            await _forgejo.DisposeAsync();
        }
    }

    [DockerFact]
    public async Task A_functions_repository_is_edited_committed_and_merged_through_the_ide_under_its_own_permissions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = Host();
        var client = host.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var instanceId = await InstallAsync(client, slug, ct);

        // The plugin asks for its repository, bound to its own permissions.
        RepoInfo? repo = null;
        var ran = await host.Services.GetRequiredService<PluginHandlerRunner>().RunAsync(tenantId, "functions", instanceId, async (ctx, _) =>
            repo = await ctx.Contracts.Get<IPluginRepos>().EnsureAsync(
                new RepoSetup("read", "edit", "deploy", new Dictionary<string, string> { ["functions/hello.ts"] = "export const v = 1;\n" }), ct), ct);
        ran.Should().BeTrue();
        repo!.RepoFullName.Should().EndWith($"/functions-{instanceId}");

        // Binding to another plugin's permission is refused.
        var steal = () => host.Services.GetRequiredService<PluginHandlerRunner>().RunAsync(tenantId, "functions", instanceId, (ctx, _) =>
            ctx.Contracts.Get<IPluginRepos>().EnsureAsync(new RepoSetup("plugin:dynamic-apps:publish", "edit", "deploy"), ct), ct);
        await steal.Should().ThrowAsync<Exception>();

        var owner = await SeedMemberAsync(tenantId, "Owner", ct);
        var member = await SeedMemberAsync(tenantId, "Member", ct);
        var at = $"/api/admin/repos/{repo.Id}";

        // A plain member holds none of the Functions permissions.
        (await client.SendAsync(Req(HttpMethod.Get, $"{at}/ide", slug, member), ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var ide = await Json(await client.SendAsync(Req(HttpMethod.Get, $"{at}/ide", slug, owner), ct), ct);
        ide.GetProperty("branch").GetString().Should().Be("main");
        ide.GetProperty("files").GetProperty("functions/hello.ts").GetString().Should().Be("export const v = 1;\n");
        var hash = ide.GetProperty("hashes").GetProperty("functions/hello.ts").GetString();

        (await client.SendAsync(Req(HttpMethod.Patch, $"{at}/ide/files", slug, owner, new
        {
            put = new Dictionary<string, object>
            {
                ["functions/hello.ts"] = new { content = "export const v = 2;\n", baseHash = hash },
                ["lib/util.ts"] = new { content = "export {};\n", baseHash = (string?)null },
            },
        }), ct)).EnsureSuccessStatusCode();

        var changes = await Json(await client.SendAsync(Req(HttpMethod.Get, $"{at}/git/changes", slug, owner), ct), ct);
        changes.EnumerateArray().Select(c => $"{c.GetProperty("path").GetString()}:{c.GetProperty("status").GetString()}")
            .Should().BeEquivalentTo("functions/hello.ts:modified", "lib/util.ts:added");

        (await client.SendAsync(Req(HttpMethod.Post, $"{at}/git/commit", slug, owner, new { message = "Second version" }), ct))
            .EnsureSuccessStatusCode();
        var history = await Json(await client.SendAsync(Req(HttpMethod.Get, $"{at}/git/history", slug, owner), ct), ct);
        history.EnumerateArray().First().GetProperty("message").GetString().Should().StartWith("Second version");

        // Committing straight onto release takes the publish permission too, however the request
        // names the branch (an empty newBranch once slipped past a route-level check).
        var editor = await SeedEditorAsync(tenantId, ct);
        (await client.SendAsync(Req(HttpMethod.Get, $"{at}/ide", slug, editor), ct)).EnsureSuccessStatusCode();
        (await client.SendAsync(Req(HttpMethod.Post, $"{at}/git/commit", slug, editor,
            new { message = "Sneak", newBranch = "", targetBranch = "release" }), ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Shipping to release takes the publish permission the plugin named ("deploy").
        var merged = await Json(await client.SendAsync(Req(HttpMethod.Post, $"{at}/git/merge", slug, owner, new { head = "main" }), ct), ct);
        merged.GetProperty("merged").GetBoolean().Should().BeTrue();

        // Another tenant cannot see it.
        var (_, otherSlug) = await CreateTenantAsync(client, ct);
        (await client.SendAsync(Req(HttpMethod.Get, $"{at}/git", otherSlug, Guid.Empty, superAdmin: true), ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task A_push_to_a_plugin_repository_is_announced_to_plugins_not_built_as_a_site()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = Host();
        var client = host.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var instanceId = await InstallAsync(client, slug, ct);
        RepoInfo? repo = null;
        await host.Services.GetRequiredService<PluginHandlerRunner>().RunAsync(tenantId, "functions", instanceId, async (ctx, _) =>
            repo = await ctx.Contracts.Get<IPluginRepos>().EnsureAsync(new RepoSetup("read", "edit", "deploy"), ct), ct);

        await using var nats = new NatsClient(host.Services.GetRequiredService<IConfiguration>()["Nats:Url"]!);
        var js = nats.CreateJetStreamContext();
        await js.CreateOrUpdateStreamAsync(new StreamConfig("PLUGIN_EVENTS", ["plugins.events.>"]), ct);
        var consumer = await js.CreateOrUpdateConsumerAsync("PLUGIN_EVENTS",
            new ConsumerConfig($"repo-test-{Guid.NewGuid():N}") { FilterSubject = "plugins.events.dcms.dcms.repo.pushed", DeliverPolicy = ConsumerConfigDeliverPolicy.New }, ct);

        var body = JsonSerializer.Serialize(new { @ref = "refs/heads/release", after = "abc123", repository = new { full_name = repo!.RepoFullName } });
        var push = new HttpRequestMessage(HttpMethod.Post, "/api/internal/git/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        push.Headers.Add("X-Gitea-Signature", Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant());
        var res = await client.SendAsync(push, ct);
        (await res.Content.ReadAsStringAsync(ct)).Should().Contain("functions");

        var next = await consumer.NextAsync<PluginEventPublished>(opts: new NatsJSNextOpts { Expires = TimeSpan.FromSeconds(10) }, cancellationToken: ct);
        next.Should().NotBeNull();
        var pushed = JsonSerializer.Deserialize<RepoPushed>(next!.Data!.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        pushed.Should().BeEquivalentTo(new { InstanceId = instanceId, PluginId = "functions", Branch = "release", Sha = "abc123" });
        next.Data!.TenantId.Should().Be(tenantId);
    }

    private WebApplicationFactory<AdminApiApp::Program> Host() => fixture.Factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Forgejo:BaseUrl", _forgejo!.BaseUrl);
        builder.UseSetting("Forgejo:Token", _forgejo.Token);
        builder.UseSetting("Forgejo:WebhookSecret", Secret);
        // Forgejo cannot reach the test host; the webhook is posted by the test instead.
        builder.UseSetting("Forgejo:WebhookUrl", "http://127.0.0.1:9/hook");
    });

    private static HttpRequestMessage Req(HttpMethod method, string url, string slug, Guid user, object? body = null, bool superAdmin = false)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", (user == Guid.Empty ? Guid.NewGuid() : user).ToString());
        if (superAdmin) req.Headers.Add("X-Test-Roles", "SuperAdmin");
        req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        res.IsSuccessStatusCode.Should().BeTrue(text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<(Guid TenantId, string Slug)> CreateTenantAsync(HttpClient client, CancellationToken ct)
    {
        var slug = "repo-" + Guid.NewGuid().ToString("N")[..8];
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Headers = { { "X-Test-Sub", Guid.NewGuid().ToString() }, { "X-Test-Roles", "SuperAdmin" } },
            Content = JsonContent.Create(new { slug, name = slug, ownerUserId = Guid.NewGuid(), ownerEmail = $"{slug}@dcms.test" }),
        };
        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return ((await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tenantId").GetGuid(), slug);
    }

    private static async Task<Guid> InstallAsync(HttpClient client, string slug, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", slug, Guid.Empty,
            new { pluginId = "functions", slug = "fn", name = "Functions" }, superAdmin: true), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        res.IsSuccessStatusCode.Should().BeTrue(text);
        return JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>A member of a role holding the Functions edit permission and nothing that publishes.</summary>
    private async Task<Guid> SeedEditorAsync(Guid tenantId, CancellationToken ct)
    {
        var userId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var insert = conn.CreateCommand();
        insert.CommandText = """
            WITH role AS (
                INSERT INTO tenancy.tenant_roles ("Id", "TenantId", "EnvironmentId", "Name", "IsSystem")
                VALUES (gen_random_uuid(), @tenant, @tenant, 'Function editor', false) RETURNING "Id"),
            grant_edit AS (
                INSERT INTO tenancy.tenant_role_permissions ("Id", "TenantId", "EnvironmentId", "TenantRoleId", "Permission")
                SELECT gen_random_uuid(), @tenant, @tenant, "Id", 'plugin:functions:edit' FROM role),
            membership AS (
                INSERT INTO tenancy.tenant_memberships ("Id", "TenantId", "UserId", "Email", "CreatedAt")
                VALUES (gen_random_uuid(), @tenant, @user, @email, now()) RETURNING "Id")
            INSERT INTO tenancy.member_roles ("Id", "TenantId", "EnvironmentId", "MembershipId", "TenantRoleId")
            SELECT gen_random_uuid(), @tenant, @tenant, membership."Id", role."Id" FROM membership, role;
            """;
        insert.Parameters.AddWithValue("tenant", tenantId);
        insert.Parameters.AddWithValue("user", userId);
        insert.Parameters.AddWithValue("email", $"editor-{userId:N}@dcms.test");
        await insert.ExecuteNonQueryAsync(ct);
        return userId;
    }

    /// <summary>A member holding one seeded role, in SQL: membership only comes from an invitation otherwise.</summary>
    private async Task<Guid> SeedMemberAsync(Guid tenantId, string roleName, CancellationToken ct)
    {
        var userId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO tenancy.tenant_memberships ("Id", "TenantId", "UserId", "Email", "CreatedAt")
            VALUES (@id, @tenant, @user, @email, now());
            INSERT INTO tenancy.member_roles ("Id", "TenantId", "EnvironmentId", "MembershipId", "TenantRoleId")
            SELECT gen_random_uuid(), @tenant, r."EnvironmentId", @id, r."Id" FROM tenancy.tenant_roles r
            WHERE r."TenantId" = @tenant AND r."Name" = @role;
            """;
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("tenant", tenantId);
        insert.Parameters.AddWithValue("user", userId);
        insert.Parameters.AddWithValue("email", $"member-{userId:N}@dcms.test");
        insert.Parameters.AddWithValue("role", roleName);
        await insert.ExecuteNonQueryAsync(ct);
        return userId;
    }
}
