using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.IntegrationTests.DynamicApps;

namespace Dcms.IntegrationTests.UserAuth;

/// <summary>
/// The assistant's reach into User Authentication (ADR 0022): groups, roles and grants, sign-in
/// providers and site rules on the AI plane — only once the instance is opted in to AI tools,
/// under the member's own permissions, with provider secrets going in and never coming out.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class AiContractTests(ContentFlowFixture fixture)
{
    [DockerFact]
    public async Task An_agent_sets_up_groups_and_least_privilege_roles_under_the_members_permissions()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.InstallAsync("user-auth", "users", "{}", ct);
        var tenantId = await TenantIdAsync(app, ct);

        // Not opted in: none of it exists on the AI plane.
        (await Call(app, "users.directory", "CreateGroup", new { name = "Sales" }, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await OptInAsync(app, ct);
        (await CatalogAsync(app, app.Owner, ct)).Should().Contain(["users.directory/CreateGroup", "users.roles/CreateRole", "users.roles/Grant",
            "users.realm/SetSiteRules", "users.realm/SetProvider"]);

        // Five groups, as the operator asked for.
        var groups = new List<Guid>();
        foreach (var name in new[] { "Sales", "Support", "Finance", "Managers", "Partners" })
        {
            groups.Add((await Json(await Call(app, "users.directory", "CreateGroup", new { name }, ct), ct)).GetProperty("id").GetGuid());
        }
        var renamed = await Json(await Call(app, "users.directory", "UpdateGroup", new { groupId = groups[4], name = "External partners" }, ct), ct);
        renamed.GetProperty("name").GetString().Should().Be("External partners");

        (await Call(app, "users.roles", "CreateRole", new { key = "sales_reader", name = "Sales", permissions = new[] { "not a permission" } }, ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "the same validation as the console");
        var role = await Json(await Call(app, "users.roles", "CreateRole",
            new { key = "sales_reader", name = "Sales reader", permissions = new[] { "dynamic-apps:crm:table:deals:read" } }, ct), ct);
        role.GetProperty("bypassRowAccess").GetBoolean().Should().BeFalse("row rules still apply unless asked otherwise");
        var roleId = role.GetProperty("id").GetGuid();
        var grant = await Json(await Call(app, "users.roles", "Grant", new { roleId, subjectType = "group", subjectId = groups[0] }, ct), ct);
        (await Json(await Call(app, "users.roles", "Grant", new { roleId, subjectType = "group", subjectId = groups[0] }, ct), ct))
            .GetProperty("grantId").GetGuid().Should().Be(grant.GetProperty("grantId").GetGuid(), "granting twice is the same grant");

        var roles = await Json(await Call(app, "users.roles", "ListRoles", new { }, ct), ct);
        var listed = roles.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == roleId);
        listed.GetProperty("grants")[0].GetProperty("subjectId").GetGuid().Should().Be(groups[0]);
        (await Call(app, "users.roles", "ListResources", new { }, ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        // What the console sees is what the agent did.
        var console = await Json(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/plugins/users/roles", app.Owner, app.Tenant), ct), ct);
        console[0].GetProperty("grants")[0].GetProperty("subjectType").GetString().Should().Be("group");

        // A group that goes takes the grants to it along.
        (await Call(app, "users.directory", "DeleteGroup", new { groupId = groups[0] }, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        roles = await Json(await Call(app, "users.roles", "ListRoles", new { }, ct), ct);
        roles.GetProperty("roles")[0].GetProperty("grants").GetArrayLength().Should().Be(0);

        // A reader gets the reads, and not the writes — in the catalog, and when the call is made anyway.
        var reader = await app.AddMemberAsync(["plugin:user-auth:users-read"], ct);
        (await CatalogAsync(app, reader, ct)).Should().Contain("users.roles/ListRoles").And.NotContain("users.roles/CreateRole")
            .And.NotContain("users.realm/SetProvider");
        (await Call(app, "users.roles", "CreateRole", new { key = "sneaky", name = "Sneaky" }, ct, @as: reader))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // A provider's client secret goes to identity and never comes back.
        var saved = await Call(app, "users.realm", "SetProvider",
            new { key = "google", kind = "google", displayName = "Google", clientId = "client-1", clientSecret = "s3cret-value" }, ct);
        var savedText = await saved.Content.ReadAsStringAsync(ct);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, savedText);
        savedText.Should().NotContain("s3cret-value").And.Contain("\"hasSecret\":true");
        fixture.Realms.ProviderSecrets["google"].Should().Be("s3cret-value");
        (await (await Call(app, "users.realm", "ListProviders", new { }, ct)).Content.ReadAsStringAsync(ct)).Should().NotContain("s3cret-value");
        (await Call(app, "users.realm", "SetProvider",
                new { key = "google", kind = "oidc", displayName = "Google", clientId = "client-1", issuer = "https://evil.test" }, ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "a stored secret never follows a provider somewhere new");

        // An environment that leaves the role to deployments refuses the agent as it refuses the console.
        await ManageAsync(tenantId, "role:locked", ct);
        (await Call(app, "users.roles", "CreateRole", new { key = "locked", name = "Locked" }, ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private static async Task OptInAsync(AppHarness app, CancellationToken ct)
    {
        var instances = await Json(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/plugins/instances", app.Owner, app.Tenant), ct), ct);
        var id = instances.EnumerateArray().Single(i => i.GetProperty("slug").GetString() == "users").GetProperty("id").GetGuid();
        var enabled = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Put, $"/api/admin/plugins/instances/{id}", app.Owner, app.Tenant,
            body: new { aiToolsEnabled = true }), ct);
        enabled.IsSuccessStatusCode.Should().BeTrue(await enabled.Content.ReadAsStringAsync(ct));
    }

    private static Task<HttpResponseMessage> Call(AppHarness app, string contract, string op, object input, CancellationToken ct, Guid? @as = null) =>
        app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/contracts/{contract}@1/{op}?instance=users&plane=ai", @as ?? app.Owner, app.Tenant,
            body: input), ct);

    private static async Task<List<string>> CatalogAsync(AppHarness app, Guid member, CancellationToken ct)
    {
        var catalog = await Json(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/contracts?plane=ai", member, app.Tenant), ct), ct);
        return catalog.EnumerateArray()
            .Where(c => c.GetProperty("id").GetString()!.StartsWith("users.", StringComparison.Ordinal))
            .SelectMany(c => c.GetProperty("operations").EnumerateArray()
                .Select(o => $"{c.GetProperty("id").GetString()!.Split('@')[0]}/{o.GetProperty("name").GetString()}"))
            .ToList();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        response.IsSuccessStatusCode.Should().BeTrue(text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<Guid> TenantIdAsync(AppHarness app, CancellationToken ct)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "Id"::uuid FROM tenancy.tenants WHERE "Identifier" = @p""";
        cmd.Parameters.AddWithValue("p", app.Tenant);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task ManageAsync(Guid tenantId, string itemKey, CancellationToken ct)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenancy.environment_policies ("Id","TenantId","EnvironmentId","Kind","ItemKey","Mode")
            VALUES (gen_random_uuid(), @t, @t, 'user-auth', @k, 'Managed')
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("k", itemKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
