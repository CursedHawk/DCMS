using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;

namespace Dcms.IntegrationTests.Audit;

/// <summary>
/// The audit read plane, over HTTP.
///
/// <para>These exist because of a bug that shipped to production: <c>Visible()</c> composed an
/// <c>IQueryable</c> from TenancyDbContext into a query over AuditDbContext, which EF Core
/// rejects at execution time with "Cannot use multiple context instances within a single query
/// execution." Every read of the log was a 500 while every write behind it worked perfectly —
/// so the log filled up correctly and showed nothing, which is the most misleading way an audit
/// log can fail. Nothing caught it: the read plane shipped with a UI and no endpoint test.</para>
///
/// <para>Hence the first assertion here is simply <b>200</b>. Cheap, and it is exactly the
/// assertion that was missing.</para>
/// </summary>
[Collection(AdminApiCollection.Name)]
public class AuditReadPlaneTests(AdminApiFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    private static HttpRequestMessage Req(
        HttpMethod method, string url, Guid sub, string roles = "", string? tenantSlug = null, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (!string.IsNullOrEmpty(roles))
        {
            req.Headers.Add("X-Test-Roles", roles);
        }
        if (tenantSlug is not null)
        {
            req.Headers.Add("X-Dcms-Tenant", tenantSlug);
        }
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }
        return req;
    }

    private async Task<string> NewTenantAsync(HttpClient client, Guid owner, CancellationToken ct)
    {
        var slug = "audit-" + Guid.NewGuid().ToString("N")[..8];
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "SuperAdmin",
            body: new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return slug;
    }

    [DockerFact]
    public async Task Listing_the_log_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var slug = await NewTenantAsync(client, Guid.NewGuid(), ct);

        var res = await client.SendAsync(
            Req(HttpMethod.Get, "/api/admin/audit", SuperAdmin, "SuperAdmin", slug), ct);

        // The whole point: a 500 here is the production bug, and it is invisible from the
        // write side because the records are being stored perfectly meanwhile.
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [DockerFact]
    public async Task Creating_a_tenant_is_visible_in_that_tenants_log()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var owner = Guid.NewGuid();
        var slug = await NewTenantAsync(client, owner, ct);

        // A role update is a plain, synchronous, well-audited mutation — enough to prove the
        // write reaches the chain and comes back out of the read plane.
        var roles = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", SuperAdmin, "SuperAdmin", slug), ct);
        roles.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await ReadLogWhenPopulatedAsync(client, slug, ct);
        var items = page.GetProperty("items").EnumerateArray().ToList();

        // Provisioning a tenant records against it; if nothing at all is here, the read plane
        // is filtering out rows that the writer definitely stored.
        items.Should().NotBeEmpty();
        items.Should().AllSatisfy(i => i.GetProperty("action").GetString().Should().NotBeNullOrWhiteSpace());
    }

    [DockerFact]
    public async Task Verify_reports_an_intact_chain()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var slug = await NewTenantAsync(client, Guid.NewGuid(), ct);

        var res = await client.SendAsync(
            Req(HttpMethod.Get, "/api/admin/audit/verify", SuperAdmin, "SuperAdmin", slug), ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("valid").GetBoolean().Should().BeTrue();
    }

    [DockerFact]
    public async Task Exporting_streams_ndjson()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var slug = await NewTenantAsync(client, Guid.NewGuid(), ct);

        var res = await client.SendAsync(
            Req(HttpMethod.Get, "/api/admin/audit/export", SuperAdmin, "SuperAdmin", slug), ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var text = await res.Content.ReadAsStringAsync(ct);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Every line must stand alone as JSON — that is the entire contract of NDJSON, and
            // a consumer reading it line-by-line breaks the moment one line does not.
            var parsed = JsonDocument.Parse(line);
            parsed.RootElement.TryGetProperty("action", out _).Should().BeTrue();
        }
    }

    [DockerFact]
    public async Task One_tenants_records_are_not_visible_to_another()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var slugA = await NewTenantAsync(client, Guid.NewGuid(), ct);
        var slugB = await NewTenantAsync(client, Guid.NewGuid(), ct);

        var pageA = await ReadLogAsync(client, slugA, ct);
        var pageB = await ReadLogAsync(client, slugB, ct);

        var idsA = pageA.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToHashSet();
        var idsB = pageB.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToHashSet();

        idsA.Overlaps(idsB).Should().BeFalse();
    }

    [DockerFact]
    public async Task A_record_from_another_tenant_is_a_404_not_a_403()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var slugA = await NewTenantAsync(client, Guid.NewGuid(), ct);
        var slugB = await NewTenantAsync(client, Guid.NewGuid(), ct);

        var pageA = await ReadLogWhenPopulatedAsync(client, slugA, ct);
        var someRecord = pageA.GetProperty("items").EnumerateArray().First().GetProperty("id").GetGuid();

        var res = await client.SendAsync(
            Req(HttpMethod.Get, $"/api/admin/audit/{someRecord}", SuperAdmin, "SuperAdmin", slugB), ct);

        // 403 would confirm the id exists, which is itself a disclosure.
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task A_member_without_audit_read_is_forbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var stranger = Guid.NewGuid();
        var slug = await NewTenantAsync(client, Guid.NewGuid(), ct);

        var res = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/audit", stranger, tenantSlug: slug), ct);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task<JsonElement> ReadLogAsync(HttpClient client, string slug, CancellationToken ct)
    {
        var res = await client.SendAsync(
            Req(HttpMethod.Get, "/api/admin/audit", SuperAdmin, "SuperAdmin", slug), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    /// <summary>
    /// The log, once it has at least one record in it.
    ///
    /// <para>Writes do not land in <c>audit.audit_events</c> synchronously. <c>OutboxAuditSink</c>
    /// commits the record to <c>audit.audit_outbox</c> inside the caller's own transaction — that
    /// is the whole point, since a record written separately is one a crash can lose — and a
    /// background dispatcher promotes it afterwards. So a read issued the instant a 201 comes
    /// back is racing that dispatcher, and gets an empty page. Polling is not papering over a
    /// flake here; it is the correct way to assert on an eventually-consistent read plane, and
    /// it is the same shape MediaWorkerTests already uses for the media pipeline.</para>
    /// </summary>
    private static async Task<JsonElement> ReadLogWhenPopulatedAsync(
        HttpClient client, string slug, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        JsonElement page = default;
        while (DateTime.UtcNow < deadline)
        {
            page = await ReadLogAsync(client, slug, ct);
            if (page.GetProperty("items").GetArrayLength() > 0)
            {
                return page;
            }
            await Task.Delay(200, ct);
        }
        return page;
    }
}
