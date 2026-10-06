using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.DynamicApps;

/// <summary>
/// Configuration revisions through the admin routes (ADR 0021): draft → publish → change →
/// publish → rollback, the optimistic-concurrency guarantees, a refused publish, permissions,
/// audit and tenant isolation — against the real admin host and database.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class ConfigurationRevisionTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    private const string Crm = """
        [
          { "op": "create", "type": "table", "value": { "apiName": "companies", "displayName": "Company", "primaryFieldId": "name",
              "fields": [ { "apiName": "name", "displayName": "Name", "type": "text", "required": true } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "contacts", "displayName": "Contact", "primaryFieldId": "email",
              "fields": [ { "apiName": "email", "displayName": "Email", "type": "email", "unique": true } ] } },
          { "op": "create", "type": "relationship", "value": { "apiName": "company", "sourceTableId": "contacts",
              "targetTableId": "companies", "inverseApiName": "contacts" } }
        ]
        """;

    [DockerFact]
    public async Task A_draft_is_published_changed_published_again_and_rolled_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, owner, tenant) = await AppAsync(ct);

        var state = await Json(admin, Req(HttpMethod.Get, "/api/admin/plugins/crm/_model", owner, tenant), ct);
        state.GetProperty("draft").ValueKind.Should().Be(JsonValueKind.Null);
        var emptyHash = state.GetProperty("hash").GetString()!;

        // A stale hash is a conflict, never an overwrite.
        (await admin.SendAsync(Changes(owner, tenant, "0000", Crm), ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // The first change opens the draft.
        var applied = await Json(admin, Changes(owner, tenant, emptyHash, Crm), ct);
        var r1 = applied.GetProperty("draft");
        r1.GetProperty("number").GetInt32().Should().Be(1);
        r1.GetProperty("status").GetString().Should().Be("draft");
        applied.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("path").GetString())
            .Should().Contain(["companies", "contacts.email", "contacts.company"]);
        var r1Hash = r1.GetProperty("hash").GetString()!;

        var preview = await Json(admin, Req(HttpMethod.Get, "/api/admin/plugins/crm/_model/draft/preview", owner, tenant), ct);
        preview.GetProperty("canPublish").GetBoolean().Should().BeTrue(preview.ToString());

        (await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/crm/_model/draft/publish", owner, tenant,
            body: new { expectedHash = emptyHash }), ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var published = await Json(admin, Req(HttpMethod.Post, "/api/admin/plugins/crm/_model/draft/publish", owner, tenant,
            body: new { expectedHash = r1Hash }), ct);
        published.GetProperty("revision").GetProperty("status").GetString().Should().Be("published");

        // The next change opens r2 from r1, with r1's hash as its starting point.
        var r2 = (await Json(admin, Changes(owner, tenant, r1Hash, """
            [ { "op": "create", "type": "field", "target": "companies", "value": { "apiName": "revenue", "displayName": "Revenue", "type": "decimal" } } ]
            """), ct)).GetProperty("draft");
        r2.GetProperty("number").GetInt32().Should().Be(2);
        r2.GetProperty("basePublishedId").GetGuid().Should().Be(r1.GetProperty("id").GetGuid());
        (await Json(admin, Req(HttpMethod.Get, "/api/admin/plugins/crm/_model/published", owner, tenant), ct))
            .GetProperty("revision").GetProperty("number").GetInt32().Should().Be(1, "a draft never changes what is live");

        await Json(admin, Req(HttpMethod.Post, "/api/admin/plugins/crm/_model/draft/publish", owner, tenant,
            body: new { expectedHash = r2.GetProperty("hash").GetString() }), ct);

        // Rollback is a new revision with r1's content; r2 stays, marked rolled back.
        var rolled = await Json(admin, Req(HttpMethod.Post, "/api/admin/plugins/crm/_model/rollback", owner, tenant,
            body: new { toRevision = 1 }), ct);
        var r3 = rolled.GetProperty("revision");
        r3.GetProperty("number").GetInt32().Should().Be(3);
        r3.GetProperty("hash").GetString().Should().Be(r1Hash);
        r3.GetProperty("parentId").GetGuid().Should().Be(r1.GetProperty("id").GetGuid());

        var revisions = await Json(admin, Req(HttpMethod.Get, "/api/admin/plugins/crm/_model/revisions", owner, tenant), ct);
        revisions.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("status").GetString())
            .Should().Equal("published", "rolledBack", "superseded");

        var diff = await Json(admin, Req(HttpMethod.Get, "/api/admin/plugins/crm/_model/diff?from=2&to=3", owner, tenant), ct);
        diff.GetProperty("changes").EnumerateArray().Single().GetProperty("path").GetString().Should().Be("companies.revenue");
        (await Json(admin, Req(HttpMethod.Get, "/api/admin/plugins/crm/_model/revisions/3/changes", owner, tenant), ct))
            .EnumerateArray().Single().GetProperty("op").GetString().Should().Be("delete");

        await AuditedAsync("plugin.dynamic-apps.revision.rolled_back", ct);
    }

    [DockerFact]
    public async Task An_invalid_draft_is_refused_publication_and_stays_editable()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, owner, tenant) = await AppAsync(ct);
        var hash = (await Json(admin, Req(HttpMethod.Get, "/api/admin/plugins/crm/_model", owner, tenant), ct)).GetProperty("hash").GetString()!;

        var applied = await Json(admin, Changes(owner, tenant, hash, """
            [ { "op": "create", "type": "table", "value": { "apiName": "Bad Name", "displayName": "Bad" } } ]
            """), ct);
        applied.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("code").GetString()).Should().Contain("invalid-api-name");
        var draftHash = applied.GetProperty("draft").GetProperty("hash").GetString();

        var refused = await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/crm/_model/draft/publish", owner, tenant,
            body: new { expectedHash = draftHash }), ct);
        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("published").GetBoolean().Should().BeFalse();

        var state = await Json(admin, Req(HttpMethod.Get, "/api/admin/plugins/crm/_model", owner, tenant), ct);
        state.GetProperty("published").ValueKind.Should().Be(JsonValueKind.Null);
        state.GetProperty("draft").GetProperty("hash").GetString().Should().Be(draftHash);
    }

    [DockerFact]
    public async Task Two_writers_on_the_same_hash_cannot_both_win()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, owner, tenant) = await AppAsync(ct);
        var hash = (await Json(admin, Req(HttpMethod.Get, "/api/admin/plugins/crm/_model", owner, tenant), ct)).GetProperty("hash").GetString()!;

        var writers = Enumerable.Range(0, 4).Select(i => admin.SendAsync(Changes(owner, tenant, hash, $$"""
            [ { "op": "create", "type": "table", "value": { "apiName": "t{{i}}", "displayName": "T{{i}}" } } ]
            """), ct));
        var codes = (await Task.WhenAll(writers)).Select(r => r.StatusCode).ToList();

        codes.Count(c => c == HttpStatusCode.OK).Should().Be(1);
        codes.Count(c => c == HttpStatusCode.Conflict).Should().Be(3);
        var draft = await Json(admin, Req(HttpMethod.Get, "/api/admin/plugins/crm/_model/draft", owner, tenant), ct);
        draft.GetProperty("config").GetProperty("tables").GetArrayLength().Should().Be(1);
    }

    [DockerFact]
    public async Task Permissions_gate_reading_editing_and_publishing_and_tenants_do_not_meet()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, owner, tenant) = await AppAsync(ct);
        var hash = (await Json(admin, Req(HttpMethod.Get, "/api/admin/plugins/crm/_model", owner, tenant), ct)).GetProperty("hash").GetString()!;

        var reader = await AddMemberAsync(admin, owner, tenant, ["plugin:dynamic-apps:model-read"], ct);
        (await admin.SendAsync(Req(HttpMethod.Get, "/api/admin/plugins/crm/_model", reader, tenant), ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.SendAsync(Changes(reader, tenant, hash, Crm), ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var editor = await AddMemberAsync(admin, owner, tenant, ["plugin:dynamic-apps:model-read", "plugin:dynamic-apps:model-write"], ct);
        var draftHash = (await Json(admin, Changes(editor, tenant, hash, Crm), ct)).GetProperty("draft").GetProperty("hash").GetString();
        (await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/crm/_model/draft/publish", editor, tenant,
            body: new { expectedHash = draftHash }), ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Another tenant's app of the same slug knows nothing of this one.
        var (_, otherOwner, otherTenant) = await AppAsync(ct);
        (await Json(admin, Req(HttpMethod.Get, "/api/admin/plugins/crm/_model", otherOwner, otherTenant), ct))
            .GetProperty("draft").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- helpers ----

    private async Task<(HttpClient Admin, Guid Owner, string Tenant)> AppAsync(CancellationToken ct)
    {
        var admin = fixture.Admin.CreateClient();
        var owner = Guid.NewGuid();
        var tenant = "da-" + Guid.NewGuid().ToString("N")[..8];
        (await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "", "SuperAdmin",
            new { slug = tenant, name = tenant, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var installed = await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, tenant,
            body: new { pluginId = "dynamic-apps", slug = "crm", name = "CRM", config = """{"displayName":"Sales CRM"}""" }), ct);
        installed.StatusCode.Should().Be(HttpStatusCode.Created, await installed.Content.ReadAsStringAsync(ct));
        return (admin, owner, tenant);
    }

    private static HttpRequestMessage Changes(Guid sub, string tenant, string expectedHash, string operations) =>
        Req(HttpMethod.Post, "/api/admin/plugins/crm/_model/draft/changes", sub, tenant,
            body: new { expectedHash, operations = JsonDocument.Parse(operations).RootElement });

    private async Task AuditedAsync(string action, CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
        {
            using var scope = fixture.Admin.Services.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<Dcms.Shared.Data.Audit.AuditDbContext>();
            using var rls = Dcms.Shared.Data.Rls.RlsScope.Platform();
            if (await audit.Events.AsNoTracking().AnyAsync(e => e.Action == action, ct))
            {
                return;
            }
            await Task.Delay(250, ct);
        }
        throw new Xunit.Sdk.XunitException($"No audit record {action}.");
    }

    private static async Task<JsonElement> Json(HttpClient client, HttpRequestMessage req, CancellationToken ct)
    {
        var res = await client.SendAsync(req, ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(ct));
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task<Guid> AddMemberAsync(HttpClient admin, Guid owner, string tenant, string[] permissions, CancellationToken ct)
    {
        var roleRes = await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/roles", owner, tenant,
            body: new { name = "Limited " + Guid.NewGuid().ToString("N")[..6], permissions }), ct);
        roleRes.StatusCode.Should().Be(HttpStatusCode.Created, await roleRes.Content.ReadAsStringAsync(ct));
        var roleId = (await roleRes.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        var member = Guid.NewGuid();
        await using var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenancy.tenant_memberships ("Id", "TenantId", "UserId", "Email", "CreatedAt")
            VALUES (@mid, (SELECT "Id"::uuid FROM tenancy.tenants WHERE "Identifier" = @slug), @uid, @email, now());
            INSERT INTO tenancy.member_roles ("Id", "TenantId", "MembershipId", "TenantRoleId")
            VALUES (@mrid, (SELECT "Id"::uuid FROM tenancy.tenants WHERE "Identifier" = @slug), @mid, @rid);
            """;
        var membershipId = Guid.NewGuid();
        cmd.Parameters.AddWithValue("mid", membershipId);
        cmd.Parameters.AddWithValue("slug", tenant);
        cmd.Parameters.AddWithValue("uid", member);
        cmd.Parameters.AddWithValue("email", $"{member:N}@dcms.test");
        cmd.Parameters.AddWithValue("mrid", Guid.NewGuid());
        cmd.Parameters.AddWithValue("rid", roleId);
        await cmd.ExecuteNonQueryAsync(ct);
        return member;
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string slug, string roles = "", object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (!string.IsNullOrEmpty(roles)) req.Headers.Add("X-Test-Roles", roles);
        if (slug.Length > 0) req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }
}
