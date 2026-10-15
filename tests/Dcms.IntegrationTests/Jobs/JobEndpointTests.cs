using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Data.Jobs;
using Dcms.Shared.Jobs;
using Dcms.Shared.Security;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dcms.IntegrationTests.Jobs;

/// <summary>
/// The jobs REST surface (ADR 0024): who sees which job, a group rolled up from its children,
/// cancel and retry, and the console's cross-tenant view.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class JobEndpointTests(AdminApiFixture fixture)
{
    [DockerFact]
    public async Task A_member_sees_jobs_they_may_see_or_started_and_nothing_else()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        // The seeded Member role holds media:read but not site:publish.
        var member = await SeedMemberAsync(tenantId, "Member", ct);

        var open = await EnqueueAsync(new JobSpec("test.open", tenantId, null, "t"), ct);
        var media = await EnqueueAsync(new JobSpec("test.media", tenantId, null, "t", RequiredPermission: PlatformPermissions.MediaRead), ct);
        var publish = await EnqueueAsync(new JobSpec("test.publish", tenantId, null, "t", RequiredPermission: PlatformPermissions.SitePublish), ct);
        var mine = await EnqueueAsync(new JobSpec("test.mine", tenantId, null, "t", RequiredPermission: PlatformPermissions.SitePublish), ct);
        await SqlAsync("""UPDATE jobs.jobs SET "ActorUserId" = @u WHERE "Id" = @id""", ct, ("u", member), ("id", mine.Id));

        var ids = await ListIdsAsync(client, slug, member, superAdmin: false, ct);

        ids.Should().Contain([open.Id, media.Id, mine.Id]);
        ids.Should().NotContain(publish.Id, "a member without site:publish must not see a publish they did not start");

        (await client.SendAsync(Request(HttpMethod.Post, $"/api/admin/jobs/{publish.Id}/cancel", slug, member, false), ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "what they cannot see they cannot cancel either");
    }

    [DockerFact]
    public async Task A_member_who_may_only_look_cannot_rerun_a_drive_import()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var member = await SeedMemberAsync(tenantId, "Member", ct); // media:read, not media:write

        // A failed Drive file: retrying it downloads again with the importer's own Drive grant.
        var job = await EnqueueAsync(new JobSpec("media.drive-file", tenantId, null, "t",
            RequiredPermission: PlatformPermissions.MediaWrite), ct);
        await LedgerAsync(async l => { await l.ClaimAsync(job.Id, tenantId, ct); await l.FailAsync(job.Id, tenantId, "e", null, false, ct); });

        (await client.SendAsync(Request(HttpMethod.Post, $"/api/admin/jobs/{job.Id}/retry", slug, member, false), ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task Another_tenants_job_does_not_exist_here()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (_, slug) = await CreateTenantAsync(client, ct);
        var (otherTenant, _) = await CreateTenantAsync(client, ct);
        var foreign = await EnqueueAsync(new JobSpec("test.foreign", otherTenant, null, "t"), ct);

        (await client.SendAsync(Request(HttpMethod.Get, $"/api/admin/jobs/{foreign.Id}", slug, Guid.NewGuid()), ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.SendAsync(Request(HttpMethod.Post, $"/api/admin/jobs/{foreign.Id}/cancel", slug, Guid.NewGuid()), ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ListIdsAsync(client, slug, Guid.NewGuid(), superAdmin: true, ct)).Should().NotContain(foreign.Id);
    }

    [DockerFact]
    public async Task A_group_is_its_children()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);

        var group = await EnqueueAsync(new JobSpec("test.group", tenantId, null, "t", Lane: JobLanes.Group), ct);
        var a = await EnqueueAsync(new JobSpec("test.child", tenantId, null, "t", ParentId: group.Id), ct);
        var b = await EnqueueAsync(new JobSpec("test.child", tenantId, null, "t", ParentId: group.Id), ct);

        await LedgerAsync(async l => { await l.ClaimAsync(a.Id, tenantId, ct); await l.CompleteAsync(a.Id, tenantId, null, ct); });
        var half = await GetAsync(client, slug, group.Id, ct);
        half.GetProperty("job").GetProperty("status").GetString().Should().Be("Running");
        half.GetProperty("job").GetProperty("done").GetInt64().Should().Be(100);
        half.GetProperty("job").GetProperty("total").GetInt64().Should().Be(200);
        half.GetProperty("children").GetArrayLength().Should().Be(2);

        await LedgerAsync(async l => { await l.ClaimAsync(b.Id, tenantId, ct); await l.FailAsync(b.Id, tenantId, "e", "x", false, ct); });
        var done = await GetAsync(client, slug, group.Id, ct);
        done.GetProperty("job").GetProperty("status").GetString().Should().Be("Failed", "one child failed");
        done.GetProperty("job").GetProperty("retryable").GetBoolean().Should().BeTrue();

        // Retrying the group retries the child that failed, and the group is running again.
        (await client.SendAsync(Request(HttpMethod.Post, $"/api/admin/jobs/{group.Id}/retry", slug, Guid.NewGuid()), ct))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await GetAsync(client, slug, group.Id, ct)).GetProperty("job").GetProperty("status").GetString()
            .Should().Be("Running");

        // The list shows the group, not its children.
        var ids = await ListIdsAsync(client, slug, Guid.NewGuid(), superAdmin: true, ct);
        ids.Should().Contain(group.Id).And.NotContain([a.Id, b.Id]);
    }

    [DockerFact]
    public async Task Cancel_and_retry_answer_for_what_they_can_and_cannot_do()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var user = Guid.NewGuid();

        var queued = await EnqueueAsync(new JobSpec("test.cancel", tenantId, null, "t"), ct);
        var cancel = await client.SendAsync(Request(HttpMethod.Post, $"/api/admin/jobs/{queued.Id}/cancel", slug, user), ct);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);

        // Another queue's job: retrying it is that feature's business, not the ledger's.
        var media = await EnqueueAsync(new JobSpec("media.process.image", tenantId, null, "t", Lane: "media", Cancelable: false), ct);
        await LedgerAsync(l => l.ApplyReportAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, media.Id, tenantId,
            "Failed", null, null, null, "media.error.decode", null, null), ct));
        (await client.SendAsync(Request(HttpMethod.Post, $"/api/admin/jobs/{media.Id}/retry", slug, user), ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        var running = await EnqueueAsync(new JobSpec("test.stubborn", tenantId, null, "t", Cancelable: false), ct);
        await LedgerAsync(l => l.ClaimAsync(running.Id, tenantId, ct));
        (await client.SendAsync(Request(HttpMethod.Post, $"/api/admin/jobs/{running.Id}/cancel", slug, user), ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [DockerFact]
    public async Task An_unexpected_failure_keeps_its_detail_from_the_tenant_but_not_the_operator()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var job = await EnqueueAsync(new JobSpec("test.crash", tenantId, null, "t"), ct);
        await LedgerAsync(async l =>
        {
            await l.ClaimAsync(job.Id, tenantId, ct);
            await l.FailAsync(job.Id, tenantId, "jobs.error.failed", "Npgsql: relation \"x\" does not exist", false, ct);
        });

        var tenantView = await GetAsync(client, slug, job.Id, ct);
        tenantView.GetProperty("job").GetProperty("errorDetail").ValueKind.Should().Be(JsonValueKind.Null);

        var res = await client.SendAsync(Request(HttpMethod.Get, $"/api/admin/platform/jobs/{job.Id}", null, Guid.NewGuid()), ct);
        res.EnsureSuccessStatusCode();
        var operatorView = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        operatorView.GetProperty("job").GetProperty("errorDetail").GetString().Should().Contain("Npgsql");
        operatorView.GetProperty("job").GetProperty("tenantSlug").GetString().Should().Be(slug);
    }

    [DockerFact]
    public async Task A_job_says_who_started_it_and_only_the_operator_sees_the_trace()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var started = await EnqueueAsync(new JobSpec("test.origin", tenantId, null, "t"), ct);
        var nobody = await EnqueueAsync(new JobSpec("test.origin", tenantId, null, "t"), ct);
        // What a person's request captures (AuditPropagation), as JobQueue stores it.
        await SqlAsync("""UPDATE jobs.jobs SET "ContextJson" = @c::jsonb WHERE "Id" = @id""", ct,
            ("c", """{"Dcms-Actor-Kind":"User","Dcms-Actor-Display":"ana@dcms.test","Dcms-Trace-Id":"4bf92f3577b34da6"}"""),
            ("id", started.Id));
        await SqlAsync("""UPDATE jobs.jobs SET "ContextJson" = NULL WHERE "Id" = @id""", ct, ("id", nobody.Id));

        var origin = (await GetAsync(client, slug, started.Id, ct)).GetProperty("job").GetProperty("origin");
        origin.GetProperty("actorKind").GetString().Should().Be("User");
        origin.GetProperty("actorName").GetString().Should().Be("ana@dcms.test");
        origin.GetProperty("service").GetString().Should().NotBeNullOrEmpty();
        origin.GetProperty("traceId").ValueKind.Should().Be(JsonValueKind.Null, "a trace id is the operators' business");

        var automatic = (await GetAsync(client, slug, nobody.Id, ct)).GetProperty("job").GetProperty("origin");
        automatic.GetProperty("actorKind").GetString().Should().Be("System");
        automatic.GetProperty("actorName").ValueKind.Should().Be(JsonValueKind.Null);

        var operatorView = await GetJsonAsync(client, $"/api/admin/platform/jobs/{started.Id}", ct);
        operatorView.GetProperty("job").GetProperty("origin").GetProperty("traceId").GetString().Should().Be("4bf92f3577b34da6");
    }

    [DockerFact]
    public async Task The_console_sees_every_tenant_and_the_platform()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var tenantJob = await EnqueueAsync(new JobSpec("test.console", tenantId, null, "t"), ct);
        var platformJob = await EnqueueAsync(new JobSpec("test.console", null, null, "t", Lane: JobLanes.Platform), ct);

        var byTenant = await GetJsonAsync(client, $"/api/admin/platform/jobs?tenantId={tenantId}", ct);
        var row = byTenant.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == tenantJob.Id);
        row.GetProperty("tenantSlug").GetString().Should().Be(slug);

        var platformOnly = await GetJsonAsync(client, "/api/admin/platform/jobs?platformOnly=true&limit=100", ct);
        platformOnly.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())
            .Should().Contain(platformJob.Id).And.NotContain(tenantJob.Id);

        var overview = await GetJsonAsync(client, "/api/admin/platform/jobs/overview", ct);
        overview.GetProperty("tenants").EnumerateArray()
            .Should().Contain(t => t.GetProperty("tenantSlug").GetString() == slug && t.GetProperty("queued").GetInt32() == 1);
        overview.GetProperty("queues").EnumerateArray()
            .Should().Contain(q => q.GetProperty("stream").GetString() == "JOBS");

        // A tenant member reaching for the console's routes is refused.
        var member = await SeedMemberAsync(tenantId, "Member", ct);
        (await client.SendAsync(Request(HttpMethod.Get, "/api/admin/platform/jobs", slug, member, superAdmin: false), ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<JobRef> EnqueueAsync(JobSpec spec, CancellationToken ct)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(spec, ct);
    }

    private async Task LedgerAsync(Func<JobLedger, Task> call)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        await call(scope.ServiceProvider.GetRequiredService<JobLedger>());
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? slug, Guid userId, bool superAdmin = true)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", userId.ToString());
        if (superAdmin)
        {
            req.Headers.Add("X-Test-Roles", "SuperAdmin");
        }
        if (slug is not null)
        {
            req.Headers.Add("X-Dcms-Tenant", slug);
        }
        return req;
    }

    private static async Task<List<Guid>> ListIdsAsync(HttpClient client, string slug, Guid user, bool superAdmin, CancellationToken ct)
    {
        var res = await client.SendAsync(Request(HttpMethod.Get, "/api/admin/jobs?limit=100", slug, user, superAdmin), ct);
        res.EnsureSuccessStatusCode();
        var page = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string slug, Guid id, CancellationToken ct)
    {
        var res = await client.SendAsync(Request(HttpMethod.Get, $"/api/admin/jobs/{id}", slug, Guid.NewGuid()), ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        var res = await client.SendAsync(Request(HttpMethod.Get, url, null, Guid.NewGuid()), ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static async Task<(Guid Id, string Slug)> CreateTenantAsync(HttpClient client, CancellationToken ct)
    {
        var slug = "jobs-" + Guid.NewGuid().ToString("N")[..8];
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

    private async Task<Guid> SeedMemberAsync(Guid tenantId, string roleName, CancellationToken ct)
    {
        var userId = Guid.NewGuid();
        await SqlAsync("""
            INSERT INTO tenancy.tenant_memberships ("Id", "TenantId", "UserId", "Email", "CreatedAt")
            VALUES (@m, @tenant, @user, @email, now());
            INSERT INTO tenancy.member_roles ("Id", "TenantId", "MembershipId", "TenantRoleId")
            SELECT gen_random_uuid(), @tenant, @m, r."Id" FROM tenancy.tenant_roles r
            WHERE r."TenantId" = @tenant AND r."Name" = @role;
            """, ct, ("m", Guid.NewGuid()), ("tenant", tenantId), ("user", userId),
            ("email", $"m-{userId:N}@dcms.test"), ("role", roleName));
        return userId;
    }

    private async Task SqlAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
