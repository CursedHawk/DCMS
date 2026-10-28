extern alias AdminApiApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AdminApiApp::Dcms.AdminApi.Environments;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.UserAuth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Environments;

/// <summary>
/// Configuration moves between environments as migrations (ADR 0026, on ADR 0028's dimension): cut from what changed since
/// the baseline, every change reviewed against the receiving environment before it is applied, a
/// conflict resolved by the reviewer, the last one rolled back. The jobs that apply them are run
/// here by calling the engine they call, since the fixture keeps job lanes off.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class EnvironmentMigrationTests(AdminApiFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    private static HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string? tenantSlug = null, object? body = null, string roles = "")
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (roles.Length > 0) req.Headers.Add("X-Test-Roles", roles);
        if (tenantSlug is not null) EnvironmentTarget.Apply(req, tenantSlug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage res, CancellationToken ct)
    {
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(ct));
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private sealed record Workspace(string Prod, string Dev, Guid ProdId, Guid DevId, Guid Owner)
    {
        public TenantEnvironment ProdEnv { get; init; } = null!;
        public TenantEnvironment DevEnv { get; init; } = null!;
    }

    private async Task<Workspace> WorkspaceAsync(HttpClient client, CancellationToken ct)
    {
        var slug = "envmig-" + Guid.NewGuid().ToString("N")[..8];
        var owner = Guid.NewGuid();
        var created = await JsonAsync(await client.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, roles: "SuperAdmin",
            body: new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct), ct);
        var dev = await JsonAsync(await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, slug, new { name = "development" }), ct), ct);
        var (prodId, devId) = (created.GetProperty("tenantId").GetGuid(), dev.GetProperty("id").GetGuid());
        var ws = new Workspace(slug, $"{slug}/development", prodId, devId, owner)
        {
            ProdEnv = await EnvironmentTarget.LoadAsync(fixture.Factory.Services, prodId, prodId),
            DevEnv = await EnvironmentTarget.LoadAsync(fixture.Factory.Services, prodId, devId),
        };
        // The copy job, as jobs-worker would run it.
        await Engine(m => m.CopyAsync(ws.DevEnv, ws.ProdEnv, ct));
        return ws;
    }

    private async Task Engine(Func<EnvironmentMigrator, Task> act)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        await act(scope.ServiceProvider.GetRequiredService<EnvironmentMigrator>());
    }

    private static Task<HttpResponseMessage> Role(HttpClient client, Guid owner, string tenant, string name, string[] permissions) =>
        client.SendAsync(Req(HttpMethod.Post, "/api/admin/roles", owner, tenant, new { name, permissions }));

    private static async Task<string[]> RolePermissionsAsync(HttpClient client, Guid owner, string tenant, string name, CancellationToken ct)
    {
        var roles = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", owner, tenant), ct), ct);
        return roles.EnumerateArray().Where(r => r.GetProperty("name").GetString() == name)
            .SelectMany(r => r.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!))
            .Order().ToArray();
    }

    private static async Task<Guid> CutAsync(HttpClient client, Workspace ws, string name, CancellationToken ct)
    {
        var migration = await JsonAsync(await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments/migrations", ws.Owner, ws.Dev, new { name }), ct), ct);
        return migration.GetProperty("migrationId").GetGuid();
    }

    private static async Task<List<JsonElement>> PendingAsync(HttpClient client, Workspace ws, CancellationToken ct) =>
        (await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, $"/api/admin/environments/pending?from={ws.DevId}", ws.Owner, ws.Prod), ct), ct))
        .EnumerateArray().ToList();

    [DockerFact]
    public async Task A_change_in_development_is_cut_reviewed_and_applied_in_production()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var ws = await WorkspaceAsync(client, ct);

        // A copied environment starts with nothing unpublished.
        (await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/environments/changes", ws.Owner, ws.Dev), ct), ct))
            .EnumerateArray().Should().BeEmpty();

        (await Role(client, ws.Owner, ws.Dev, "Editors", ["content:read", "content:write"])).StatusCode.Should().Be(HttpStatusCode.Created);
        var changes = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/environments/changes", ws.Owner, ws.Dev), ct), ct);
        changes.EnumerateArray().Select(c => (c.GetProperty("id").GetString(), c.GetProperty("op").GetString()))
            .Should().Equal(("roles/Editors", "add"));

        var migration = await CutAsync(client, ws, "Add editors", ct);
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments/migrations", ws.Owner, ws.Dev, new { name = "Again" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "nothing changed since");

        // Production reviews it before anything happens.
        var pending = await PendingAsync(client, ws, ct);
        pending.Should().ContainSingle();
        var change = pending[0].GetProperty("changes")[0];
        change.GetProperty("outcome").GetString().Should().Be("apply");
        change.GetProperty("after").GetProperty("Permissions").EnumerateArray().Should().HaveCount(2);
        (await RolePermissionsAsync(client, ws.Owner, ws.Prod, "Editors", ct)).Should().BeEmpty("reviewing applies nothing");

        // Applying is a job; the request only queues it.
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments/pending/apply", ws.Owner, ws.Prod,
            new { from = ws.DevId, upTo = migration }), ct)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await Engine(m => m.ApplyAsync(ws.ProdEnv, ws.DevEnv, migration, new Dictionary<string, string>(), new EnvironmentApplier(ws.Owner, false), ct));

        (await RolePermissionsAsync(client, ws.Owner, ws.Prod, "Editors", ct)).Should().Equal("content:read", "content:write");
        (await PendingAsync(client, ws, ct)).Should().BeEmpty();
        var history = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/environments/migrations", ws.Owner, ws.Prod), ct), ct);
        history[0].GetProperty("name").GetString().Should().Be("Add editors");
        history[0].GetProperty("originName").GetString().Should().Be("development");
        // Production's baseline moved with it: the applied change is not its own to cut again.
        (await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/environments/changes", ws.Owner, ws.Prod), ct), ct))
            .EnumerateArray().Should().BeEmpty();
    }

    [DockerFact]
    public async Task A_conflict_waits_for_the_reviewer_and_the_last_migration_rolls_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var ws = await WorkspaceAsync(client, ct);
        await Role(client, ws.Owner, ws.Dev, "Editors", ["content:read"]);
        var first = await CutAsync(client, ws, "Add editors", ct);
        await Engine(m => m.ApplyAsync(ws.ProdEnv, ws.DevEnv, first, new Dictionary<string, string>(), new EnvironmentApplier(ws.Owner, false), ct));

        // Both sides change the role: development widens it, production narrows it by hand.
        var devRoles = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", ws.Owner, ws.Dev), ct), ct);
        var devEditors = devRoles.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Editors").GetProperty("id").GetGuid();
        (await client.SendAsync(Req(HttpMethod.Put, $"/api/admin/roles/{devEditors}", ws.Owner, ws.Dev,
            new { name = "Editors", permissions = new[] { "content:read", "content:publish" } }), ct)).IsSuccessStatusCode.Should().BeTrue();
        var second = await CutAsync(client, ws, "Editors publish", ct);
        var prodRoles = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", ws.Owner, ws.Prod), ct), ct);
        var prodEditors = prodRoles.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Editors").GetProperty("id").GetGuid();
        (await client.SendAsync(Req(HttpMethod.Put, $"/api/admin/roles/{prodEditors}", ws.Owner, ws.Prod,
            new { name = "Editors", permissions = new[] { "media:read" } }), ct)).IsSuccessStatusCode.Should().BeTrue();

        var pending = await PendingAsync(client, ws, ct);
        pending[0].GetProperty("changes")[0].GetProperty("outcome").GetString().Should().Be("conflict");

        var unresolved = () => Engine(m => m.ApplyAsync(ws.ProdEnv, ws.DevEnv, second, new Dictionary<string, string>(), new EnvironmentApplier(ws.Owner, false), ct));
        (await unresolved.Should().ThrowAsync<EnvironmentMigrationException>()).Which.Key.Should().Be("environments.errors.conflictUnresolved");
        (await RolePermissionsAsync(client, ws.Owner, ws.Prod, "Editors", ct)).Should().Equal("media:read");

        await Engine(m => m.ApplyAsync(ws.ProdEnv, ws.DevEnv, second,
            new Dictionary<string, string> { [$"{second}/roles/Editors"] = "overwrite" }, new EnvironmentApplier(ws.Owner, false), ct));
        (await RolePermissionsAsync(client, ws.Owner, ws.Prod, "Editors", ct)).Should().Equal("content:publish", "content:read");

        // Only the last one rolls back, and it puts back what the migration replaced.
        var older = () => Engine(m => m.RollbackAsync(ws.ProdEnv, first, new Dictionary<string, string>(), new EnvironmentApplier(ws.Owner, false), ct));
        (await older.Should().ThrowAsync<EnvironmentMigrationException>()).Which.Key.Should().Be("environments.errors.rollbackNotLast");
        await Engine(m => m.RollbackAsync(ws.ProdEnv, second, new Dictionary<string, string>(), new EnvironmentApplier(ws.Owner, false), ct));
        (await RolePermissionsAsync(client, ws.Owner, ws.Prod, "Editors", ct)).Should().Equal("content:read");
        (await PendingAsync(client, ws, ct)).Should().ContainSingle("a rolled-back migration is pending again");
    }

    [DockerFact]
    public async Task Deployments_come_only_from_a_source_the_environment_lists()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var ws = await WorkspaceAsync(client, ct);

        // Production does not deploy into development: development lists no sources.
        (await client.SendAsync(Req(HttpMethod.Get, $"/api/admin/environments/pending?from={ws.ProdId}", ws.Owner, ws.Dev), ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Nor does a tenant outside the workspace.
        (await client.SendAsync(Req(HttpMethod.Get, $"/api/admin/environments/pending?from={Guid.NewGuid()}", ws.Owner, ws.Prod), ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task Plugin_instances_and_their_settings_move_with_a_migration()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var ws = await WorkspaceAsync(client, ct);
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", ws.Owner, ws.Dev,
            new { pluginId = "blog", slug = "news", name = "News", config = "{}" }), ct)).StatusCode.Should().Be(HttpStatusCode.Created);
        var migration = await CutAsync(client, ws, "News blog", ct);

        await Engine(m => m.ApplyAsync(ws.ProdEnv, ws.DevEnv, migration, new Dictionary<string, string>(), new EnvironmentApplier(ws.Owner, false), ct));

        var instances = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/plugins/instances", ws.Owner, ws.Prod), ct), ct);
        instances.EnumerateArray().Select(i => i.GetProperty("slug").GetString()).Should().Contain("news");
    }

    [DockerFact]
    public async Task A_deployment_cannot_grant_what_its_deployer_could_not()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var ws = await WorkspaceAsync(client, ct);

        // Development widens Member, the role everyone holds, to members:manage.
        var devRoles = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", ws.Owner, ws.Dev), ct), ct);
        var member = devRoles.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Member");
        var widened = member.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).Append("members:manage").ToArray();
        (await client.SendAsync(Req(HttpMethod.Put, $"/api/admin/roles/{member.GetProperty("id").GetGuid()}", ws.Owner, ws.Dev,
            new { name = "Member", permissions = widened }), ct)).IsSuccessStatusCode.Should().BeTrue();
        var migration = await CutAsync(client, ws, "Members manage members", ct);

        // A deployer who may deploy and edit roles in production, and nothing else.
        var deployer = Guid.NewGuid();
        var role = await JsonAsync(await client.SendAsync(Req(HttpMethod.Post, "/api/admin/roles", ws.Owner, ws.Prod,
            new { name = "Deployer", permissions = new[] { "environments:promote", "roles:manage" } }), ct), ct);
        var invite = await JsonAsync(await client.SendAsync(Req(HttpMethod.Post, "/api/admin/invitations", ws.Owner, ws.Prod,
            new { email = $"{deployer:N}@dcms.test", roleIds = new[] { role.GetProperty("id").GetString() } }), ct), ct);
        await client.SendAsync(Req(HttpMethod.Post, "/api/admin/invitations/accept", deployer, body: new { token = invite.GetProperty("token").GetString() }), ct);

        var refused = () => Engine(m => m.ApplyAsync(ws.ProdEnv, ws.DevEnv, migration, new Dictionary<string, string>(),
            new EnvironmentApplier(deployer, false), ct));
        var error = (await refused.Should().ThrowAsync<EnvironmentMigrationException>()).Which;
        error.Key.Should().Be("environments.errors.notAllowed");
        error.Detail.Should().Be("members:manage");
        (await RolePermissionsAsync(client, ws.Owner, ws.Prod, "Member", ct)).Should().NotContain("members:manage");

        // The owner, who holds it, may.
        await Engine(m => m.ApplyAsync(ws.ProdEnv, ws.DevEnv, migration, new Dictionary<string, string>(),
            new EnvironmentApplier(ws.Owner, false), ct));
        (await RolePermissionsAsync(client, ws.Owner, ws.Prod, "Member", ct)).Should().Contain("members:manage");
    }

    [DockerFact]
    public async Task A_skipped_conflict_is_not_counted_as_applied_when_a_later_change_is_authorised()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var ws = await WorkspaceAsync(client, ct);
        async Task SetMember(string tenant, params string[] permissions)
        {
            var roles = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", ws.Owner, tenant), ct), ct);
            var id = roles.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Member").GetProperty("id").GetGuid();
            (await client.SendAsync(Req(HttpMethod.Put, $"/api/admin/roles/{id}", ws.Owner, tenant,
                new { name = "Member", permissions }), ct)).IsSuccessStatusCode.Should().BeTrue();
        }

        // Production narrows Member by hand, so every change to it from development conflicts.
        await SetMember(ws.Prod, "media:read");
        await SetMember(ws.Dev, "media:read", "analytics:read", "members:manage");
        var first = await CutAsync(client, ws, "Members manage members", ct);
        await SetMember(ws.Dev, "media:read", "analytics:read", "members:manage", "content:read");
        var second = await CutAsync(client, ws, "Members read content", ct);

        // A deployer who holds the second change's addition but not the first's.
        var deployer = Guid.NewGuid();
        var role = await JsonAsync(await client.SendAsync(Req(HttpMethod.Post, "/api/admin/roles", ws.Owner, ws.Prod,
            new { name = "Deployer", permissions = new[] { "environments:promote", "roles:manage", "content:read", "analytics:read" } }), ct), ct);
        var invite = await JsonAsync(await client.SendAsync(Req(HttpMethod.Post, "/api/admin/invitations", ws.Owner, ws.Prod,
            new { email = $"{deployer:N}@dcms.test", roleIds = new[] { role.GetProperty("id").GetString() } }), ct), ct);
        await client.SendAsync(Req(HttpMethod.Post, "/api/admin/invitations/accept", deployer, body: new { token = invite.GetProperty("token").GetString() }), ct);

        // Skipping the first and overwriting with the second would still put members:manage in.
        var resolutions = new Dictionary<string, string>
        {
            [$"{first}/roles/Member"] = "skip",
            [$"{second}/roles/Member"] = "overwrite",
        };
        var refused = () => Engine(m => m.ApplyAsync(ws.ProdEnv, ws.DevEnv, second, resolutions, new EnvironmentApplier(deployer, false), ct));
        (await refused.Should().ThrowAsync<EnvironmentMigrationException>()).Which.Detail.Should().Be("members:manage");
        (await RolePermissionsAsync(client, ws.Owner, ws.Prod, "Member", ct)).Should().Equal("media:read");
    }

    [DockerFact]
    public async Task Api_connections_move_without_their_key_and_user_roles_move_by_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var ws = await WorkspaceAsync(client, ct);
        using (var scope = fixture.Factory.Services.CreateScope())
        using (RlsScope.Environment(ws.ProdId, ws.DevId))
        {
            var cms = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
            cms.ApiConnections.Add(new ApiConnection
            {
                Id = Guid.NewGuid(), TenantId = ws.ProdId, Slug = "events", Name = "Events", BaseUrl = "https://api.example.test",
                AuthKind = "bearer", SecretCiphertext = "vault:v1:dev-only", Operations = ["/events"],
            });
            await cms.SaveChangesAsync(ct);
            var userAuth = scope.ServiceProvider.GetRequiredService<UserAuthDbContext>();
            userAuth.Roles.Add(new UserRole { Id = Guid.NewGuid(), TenantId = ws.ProdId, Key = "sales", Name = "Sales", Permissions = ["crm:read"] });
            await userAuth.SaveChangesAsync(ct);
        }
        var migration = await CutAsync(client, ws, "Events feed and sales role", ct);
        await Engine(m => m.ApplyAsync(ws.ProdEnv, ws.DevEnv, migration, new Dictionary<string, string>(), new EnvironmentApplier(ws.Owner, false), ct));

        using (var scope = fixture.Factory.Services.CreateScope())
        using (RlsScope.Tenant(ws.ProdId))
        {
            var connection = await scope.ServiceProvider.GetRequiredService<CmsDbContext>().ApiConnections.IgnoreTenantFilter()
                .SingleAsync(c => c.TenantId == ws.ProdId && c.Slug == "events", ct);
            connection.BaseUrl.Should().Be("https://api.example.test");
            connection.SecretCiphertext.Should().BeNull("a key is each environment's own");
            var role = await scope.ServiceProvider.GetRequiredService<UserAuthDbContext>().Roles.IgnoreTenantFilter()
                .SingleAsync(r => r.TenantId == ws.ProdId && r.Key == "sales", ct);
            role.Permissions.Should().Equal("crm:read");
        }
    }

    [DockerFact]
    public async Task A_site_keeps_its_identity_across_environments()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var ws = await WorkspaceAsync(client, ct);
        var created = await JsonAsync(await client.SendAsync(Req(HttpMethod.Post, "/api/admin/sites", ws.Owner, ws.Dev,
            new { name = "Marketing", renderMode = "StaticPrerender" }), ct), ct);
        var devSite = created.GetProperty("id").GetGuid();
        var first = await CutAsync(client, ws, "Marketing site", ct);
        await Engine(m => m.ApplyAsync(ws.ProdEnv, ws.DevEnv, first, new Dictionary<string, string>(), new EnvironmentApplier(ws.Owner, false), ct));

        async Task<List<(string Name, Guid Id)>> ProdSites() =>
            (await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/sites", ws.Owner, ws.Prod), ct), ct))
            .EnumerateArray().Select(x => (x.GetProperty("name").GetString()!, x.GetProperty("id").GetGuid())).ToList();
        var prodSites = await ProdSites();
        prodSites.Select(x => x.Name).Should().Equal("Marketing");
        prodSites[0].Id.Should().NotBe(devSite, "each environment's rows have ids of their own");

        // A rename later finds the same site, not a second one.
        using (var scope = fixture.Factory.Services.CreateScope())
        using (RlsScope.Environment(ws.ProdId, ws.DevId))
        {
            var sites = scope.ServiceProvider.GetRequiredService<Dcms.Shared.Data.Sites.SitesDbContext>();
            (await sites.Sites.IgnoreTenantFilter().SingleAsync(x => x.Id == devSite, ct)).Name = "Marketing 2026";
            await sites.SaveChangesAsync(ct);
        }
        var second = await CutAsync(client, ws, "Rename", ct);
        await Engine(m => m.ApplyAsync(ws.ProdEnv, ws.DevEnv, second, new Dictionary<string, string>(), new EnvironmentApplier(ws.Owner, false), ct));
        (await ProdSites()).Should().Equal(("Marketing 2026", prodSites[0].Id));
    }

    [DockerFact]
    public async Task Configuration_left_to_deployments_is_refused_directly_and_local_items_are_skipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var ws = await WorkspaceAsync(client, ct);
        await Role(client, ws.Owner, ws.Dev, "Editors", ["content:read"]);
        await Role(client, ws.Owner, ws.Dev, "Reviewers", ["content:read"]);
        await Role(client, ws.Owner, ws.Prod, "Reviewers", ["media:read"]);

        // Production: roles come from the pipeline, except Reviewers, which is production's own.
        (await client.SendAsync(Req(HttpMethod.Put, $"/api/admin/environments/{ws.ProdId}/policies", ws.Owner, ws.Prod, new[]
        {
            new { kind = "roles", itemKey = (string?)null, mode = "managed" },
            new { kind = "roles", itemKey = (string?)"Reviewers", mode = "local" },
        }), ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refused = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/roles", ws.Owner, ws.Prod, new { name = "Ad hoc", permissions = new[] { "content:read" } }), ct);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().Should().Be("environment_managed");
        (await Role(client, ws.Owner, ws.Dev, "Ad hoc", ["content:read"])).StatusCode.Should().Be(HttpStatusCode.Created, "development is not locked");

        var migration = await CutAsync(client, ws, "Roles", ct);
        var pending = await PendingAsync(client, ws, ct);
        pending[0].GetProperty("changes").EnumerateArray()
            .ToDictionary(c => c.GetProperty("key").GetString()!, c => c.GetProperty("outcome").GetString())
            .Should().Contain("Reviewers", "local").And.Contain("Editors", "apply");

        await Engine(m => m.ApplyAsync(ws.ProdEnv, ws.DevEnv, migration, new Dictionary<string, string>(), new EnvironmentApplier(ws.Owner, false), ct));
        (await RolePermissionsAsync(client, ws.Owner, ws.Prod, "Editors", ct)).Should().Equal("content:read");
        (await RolePermissionsAsync(client, ws.Owner, ws.Prod, "Reviewers", ct)).Should().BeEquivalentTo(["media:read"], "production keeps its own");

        // Reviewers is production's own, so editing it there is still allowed.
        var prodRoles = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", ws.Owner, ws.Prod), ct), ct);
        var reviewers = prodRoles.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Reviewers").GetProperty("id").GetGuid();
        (await client.SendAsync(Req(HttpMethod.Put, $"/api/admin/roles/{reviewers}", ws.Owner, ws.Prod,
            new { name = "Reviewers", permissions = new[] { "media:read", "analytics:read" } }), ct)).IsSuccessStatusCode.Should().BeTrue();
        // But not renamed onto a name the pipeline owns.
        (await client.SendAsync(Req(HttpMethod.Put, $"/api/admin/roles/{reviewers}", ws.Owner, ws.Prod,
            new { name = "Editors 2", permissions = new[] { "media:read" } }), ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        // Nor by naming it twice, so the check reads one name and the handler binds the other.
        var twice = Req(HttpMethod.Put, $"/api/admin/roles/{reviewers}", ws.Owner, ws.Prod);
        twice.Content = new StringContent("""{"name":"Reviewers","Name":"Editors 2","permissions":["media:read"]}""",
            System.Text.Encoding.UTF8, "application/json");
        (await client.SendAsync(twice, ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
