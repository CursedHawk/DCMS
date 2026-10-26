extern alias AdminApiApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AdminApiApp::Dcms.AdminApi.Environments;
using Dcms.IntegrationTests.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Environments;

/// <summary>
/// Configuration moves between environments as migrations (ADR 0026): cut from what changed since
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
        if (tenantSlug is not null) req.Headers.Add("X-Dcms-Tenant", tenantSlug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage res, CancellationToken ct)
    {
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(ct));
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private sealed record Workspace(string Prod, string Dev, Guid ProdId, Guid DevId, Guid Owner);

    private async Task<Workspace> WorkspaceAsync(HttpClient client, CancellationToken ct)
    {
        var slug = "envmig-" + Guid.NewGuid().ToString("N")[..8];
        var owner = Guid.NewGuid();
        var created = await JsonAsync(await client.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, roles: "SuperAdmin",
            body: new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct), ct);
        await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments/enable", owner, slug), ct);
        var dev = await JsonAsync(await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, slug, new { name = "development" }), ct), ct);
        var ws = new Workspace(slug, $"{slug}-development", created.GetProperty("tenantId").GetGuid(), dev.GetProperty("tenantId").GetGuid(), owner);
        // The copy job, as jobs-worker would run it.
        await Engine(m => m.CopyAsync(ws.DevId, ws.ProdId, ct));
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
        await Engine(m => m.ApplyAsync(ws.ProdId, ws.DevId, migration, new Dictionary<string, string>(), ws.Owner, ct));

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
        await Engine(m => m.ApplyAsync(ws.ProdId, ws.DevId, first, new Dictionary<string, string>(), ws.Owner, ct));

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

        var unresolved = () => Engine(m => m.ApplyAsync(ws.ProdId, ws.DevId, second, new Dictionary<string, string>(), ws.Owner, ct));
        (await unresolved.Should().ThrowAsync<EnvironmentMigrationException>()).Which.Key.Should().Be("environments.errors.conflictUnresolved");
        (await RolePermissionsAsync(client, ws.Owner, ws.Prod, "Editors", ct)).Should().Equal("media:read");

        await Engine(m => m.ApplyAsync(ws.ProdId, ws.DevId, second,
            new Dictionary<string, string> { ["roles/Editors"] = "overwrite" }, ws.Owner, ct));
        (await RolePermissionsAsync(client, ws.Owner, ws.Prod, "Editors", ct)).Should().Equal("content:publish", "content:read");

        // Only the last one rolls back, and it puts back what the migration replaced.
        var older = () => Engine(m => m.RollbackAsync(ws.ProdId, first, new Dictionary<string, string>(), ct));
        (await older.Should().ThrowAsync<EnvironmentMigrationException>()).Which.Key.Should().Be("environments.errors.rollbackNotLast");
        await Engine(m => m.RollbackAsync(ws.ProdId, second, new Dictionary<string, string>(), ct));
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

        await Engine(m => m.ApplyAsync(ws.ProdId, ws.DevId, migration, new Dictionary<string, string>(), ws.Owner, ct));

        var instances = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/plugins/instances", ws.Owner, ws.Prod), ct), ct);
        instances.EnumerateArray().Select(i => i.GetProperty("slug").GetString()).Should().Contain("news");
    }
}
