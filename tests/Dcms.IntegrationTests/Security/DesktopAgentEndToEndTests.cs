extern alias AdminApiApp;
extern alias CliApp;

using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Protocol;
using Npgsql;

namespace Dcms.IntegrationTests.Security;

/// <summary>
/// A desktop agent end to end (ADR 0032, C7): linked with <c>dcms link-agent</c>, it works through
/// the same tool handlers <c>dcms mcp</c> serves — pulls a Functions app into the project, edits it,
/// commits, releases it — against the real admin-api and Forgejo, with only its own token. And it
/// is held to that token: a read-only agent cannot write, an agent for production cannot reach
/// staging, a person's operations are not in its catalog, an app whose AI tools are off is out
/// of its reach.
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class DesktopAgentEndToEndTests(AdminApiFixture fixture) : IAsyncLifetime
{
    private TestForgejo? _forgejo;

    public async ValueTask InitializeAsync()
    {
        if (DockerFactAttribute.Available) _forgejo = await TestForgejo.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_forgejo is not null) await _forgejo.DisposeAsync();
    }

    private WebApplicationFactory<AdminApiApp::Program> Host() => fixture.Factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Forgejo:BaseUrl", _forgejo!.BaseUrl);
        builder.UseSetting("Forgejo:Token", _forgejo.Token);
        builder.UseSetting("Forgejo:WebhookSecret", "desktop-agent-webhook-secret");
        builder.UseSetting("Forgejo:WebhookUrl", "http://127.0.0.1:9/hook");
    });

    [DockerFact]
    public async Task An_agent_pulls_edits_commits_and_releases_an_app_and_is_held_to_its_token()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = Host();
        var client = host.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var owner = await SeedMemberAsync(tenantId, "Owner", ct);
        var app = await InstallAsync(client, slug, "fn", ct);
        await InstallAsync(client, slug, "fn-off", ct); // its AI tools stay off
        (await client.SendAsync(Req(HttpMethod.Put, $"/api/admin/plugins/instances/{app}", slug, Guid.Empty, new { aiToolsEnabled = true }, superAdmin: true), ct))
            .EnsureSuccessStatusCode();
        (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/plugins/fn/_functions/repo", slug, owner), ct)).EnsureSuccessStatusCode();
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", slug, owner, new { name = "staging", empty = true }), ct))
            .EnsureSuccessStatusCode();

        var project = Directory.CreateTempSubdirectory("dcms-project-").FullName;
        var configDir = Directory.CreateTempSubdirectory("dcms-config-").FullName;
        try
        {
            var config = new CliApp::Dcms.Cli.Config(configDir);
            config.WriteCredentials(new CliApp::Dcms.Cli.Credentials("http://localhost", "http://localhost/connect/token", "token", null, long.MaxValue / 2));
            // The person's own calls (link-agent mints with them) by test header; an agent's carry its token.
            using var http = new HttpClient(new AsUser(owner) { InnerHandler = host.Server.CreateHandler() });
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var api = new CliApp::Dcms.Cli.Api(config, http, stdout);
            var commands = new CliApp::Dcms.Cli.Commands(config, api, http, stdout, stderr, new StringReader(""), project);

            async Task<CliApp::Dcms.Cli.AgentLink> Link(string name, params string[] more)
            {
                api.Agent = null;
                var code = await commands.Build().Parse(["link-agent", "--generic", "-w", slug, "--name", name, .. more]).InvokeAsync(cancellationToken: ct);
                code.Should().Be(0, $"{stderr}{stdout}");
                return config.ReadAgents().Single(a => a.Name == name);
            }
            async Task<(bool Error, string Text)> Call(CliApp::Dcms.Cli.AgentLink agent, string tool, object? args = null)
            {
                api.Agent = agent;
                var arguments = args is null ? new Dictionary<string, JsonElement>()
                    : JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
                var result = await commands.CallToolAsync(agent, new CallToolRequestParams { Name = tool, Arguments = arguments }, ct);
                return (result.IsError == true, string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
            }

            // The agent's tools: the generic ones, its everyday operations, its workspace on disk.
            var agent = await Link("Claude Code on test");
            api.Agent = agent;
            var tools = (await commands.ToolsAsync(agent, ct)).Select(t => t.Name).ToList();
            tools.Should().Contain(["dcms_find", "dcms_call", "dcms_pull", "dcms_commit", "sites_list"]);
            var whoami = await Call(agent, "dcms_whoami");
            whoami.Error.Should().BeFalse(whoami.Text);
            whoami.Text.Should().Contain("Claude Code on test");
            whoami.Text.Should().Contain("\"staging\"", "a token for every environment lists the ones it may choose");
            // Not limited to environments: every tool takes one, so the agent can work outside production.
            (await commands.ToolsAsync(agent, ct)).Single(t => t.Name == "sites_list").InputSchema
                .GetProperty("properties").TryGetProperty("environment", out _).Should().BeTrue();
            var inStaging = await Call(agent, "dcms_whoami", new { environment = "staging" });
            inStaging.Text.Should().Contain("\"environment\": \"staging\"");

            // Pull, edit, commit, release: all as the agent.
            var pulled = await Call(agent, "dcms_pull", new { target = "fn", dir = "app" });
            pulled.Error.Should().BeFalse(pulled.Text);
            var hello = Path.Combine(project, "app", "functions", "hello.ts");
            File.Exists(hello).Should().BeTrue(pulled.Text);
            File.WriteAllText(hello, File.ReadAllText(hello).Replace("Hello", "Hi"));
            (await Call(agent, "dcms_status", new { dir = "app" })).Text.Should().Contain("modified  functions/hello.ts");
            var committed = await Call(agent, "dcms_commit", new { dir = "app", message = "Say hi" });
            committed.Error.Should().BeFalse(committed.Text);
            committed.Text.Should().Contain("Committed");
            var released = await Call(agent, "dcms_publish", new { dir = "app" });
            released.Error.Should().BeFalse(released.Text);
            released.Text.Should().Contain("release");

            // A person's operation is not in its catalog; an app whose AI tools are off is out of reach.
            (await Call(agent, "dcms_call", new { name = "people.roles" })).Error.Should().BeTrue();
            var off = await Call(agent, "dcms_pull", new { target = "fn-off", dir = "off" });
            off.Error.Should().BeTrue();
            off.Text.Should().Contain("AI tools are off", "the refusal says why, not that there is no such app");
            var listed = JsonDocument.Parse((await Call(agent, "plugins_list")).Text).RootElement.EnumerateArray()
                .ToDictionary(a => a.GetProperty("slug").GetString()!, a => a.GetProperty("config").ValueKind);
            listed.Should().ContainKey("fn");
            listed["fn-off"].Should().Be(JsonValueKind.Null, "an app's settings reach an agent only where its AI tools are on");
            // A folder outside the project is out of reach on this machine too.
            (await Call(agent, "dcms_pull", new { target = "fn", dir = "../outside" })).Error.Should().BeTrue();

            // A read-only agent cannot write.
            var reader = await Link("Reader", "--read-only");
            File.WriteAllText(hello, "export {};\n");
            var push = await Call(reader, "dcms_push", new { dir = "app" });
            push.Error.Should().BeTrue();
            push.Text.Should().Contain("only read");

            // An agent for production cannot reach staging.
            var production = await Link("Production only", "--env", "production");
            (await Call(production, "dcms_whoami")).Error.Should().BeFalse();
            var staging = await Call(production, "dcms_call", new { name = "me.who", environment = "staging" });
            staging.Error.Should().BeTrue(staging.Text);
        }
        finally
        {
            Directory.Delete(project, recursive: true);
            Directory.Delete(configDir, recursive: true);
        }
    }

    private sealed class AsUser(Guid user) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("X-Test-Sub", user.ToString());
            request.Headers.Add("X-Test-Email", $"agent-owner-{user:N}@dcms.test");
            return base.SendAsync(request, cancellationToken);
        }
    }

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
        var slug = "agent-" + Guid.NewGuid().ToString("N")[..8];
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Headers = { { "X-Test-Sub", Guid.NewGuid().ToString() }, { "X-Test-Roles", "SuperAdmin" } },
            Content = JsonContent.Create(new { slug, name = slug, ownerUserId = Guid.NewGuid(), ownerEmail = $"{slug}@dcms.test" }),
        };
        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return ((await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tenantId").GetGuid(), slug);
    }

    private static async Task<Guid> InstallAsync(HttpClient client, string slug, string appSlug, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", slug, Guid.Empty,
            new { pluginId = "functions", slug = appSlug, name = appSlug }, superAdmin: true), ct);
        return (await Json(res, ct)).GetProperty("id").GetGuid();
    }

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
        insert.Parameters.AddWithValue("email", $"agent-owner-{userId:N}@dcms.test");
        insert.Parameters.AddWithValue("role", roleName);
        await insert.ExecuteNonQueryAsync(ct);
        return userId;
    }
}
