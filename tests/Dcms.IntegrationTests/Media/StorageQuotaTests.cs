using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;

namespace Dcms.IntegrationTests.Media;

/// <summary>
/// The per-tenant storage cap: 5 GB unless the console says otherwise, set only through the
/// console's endpoint, and an upload that would not fit is refused rather than stored.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class StorageQuotaTests(AdminApiFixture fixture)
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [DockerFact]
    public async Task A_new_tenant_gets_5_GB()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (_, owner, slug) = await TenantAsync(client, ct);

        (await QuotaAsync(client, owner, slug, ct)).Should().Be(5L * 1024 * 1024 * 1024);
    }

    [DockerFact]
    public async Task An_upload_past_the_cap_is_refused_until_the_cap_is_raised()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (tenantId, owner, slug) = await TenantAsync(client, ct);

        (await SetQuotaAsync(client, tenantId, Png.Length - 1, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuotaAsync(client, owner, slug, ct)).Should().Be(Png.Length - 1);

        var refused = await UploadAsync(client, owner, slug, ct);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(ct)).Should().Contain("Storage limit reached");

        (await SetQuotaAsync(client, tenantId, 1024 * 1024, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await UploadAsync(client, owner, slug, ct)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [DockerFact]
    public async Task Only_the_console_sets_the_cap()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (tenantId, owner, slug) = await TenantAsync(client, ct);

        // The tenant's own owner holds every tenant permission, and still may not raise its cap.
        var res = await client.SendAsync(Req(HttpMethod.Put, $"/api/admin/tenants/{tenantId}/storage-quota",
            owner, slug, new { quotaBytes = 1L << 40 }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await SetQuotaAsync(client, tenantId, 0, ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static Task<HttpResponseMessage> SetQuotaAsync(HttpClient client, Guid tenantId, long bytes, CancellationToken ct) =>
        client.SendAsync(Req(HttpMethod.Put, $"/api/admin/tenants/{tenantId}/storage-quota", Guid.NewGuid(), null,
            new { quotaBytes = bytes }, asSuperAdmin: true), ct);

    private static async Task<long> QuotaAsync(HttpClient client, Guid owner, string slug, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/media/usage", owner, slug), ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("quotaBytes").GetInt64();
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid owner, string slug, CancellationToken ct)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Png);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "dot.png");
        var req = Req(HttpMethod.Post, "/api/admin/media", owner, slug);
        req.Content = form;
        return client.SendAsync(req, ct);
    }

    private static async Task<(Guid TenantId, Guid Owner, string Slug)> TenantAsync(HttpClient client, CancellationToken ct)
    {
        var owner = Guid.NewGuid();
        var slug = "quota-" + Guid.NewGuid().ToString("N")[..8];
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", owner, null,
            new { slug, name = slug, ownerUserId = owner, ownerEmail = "o@dcms.test" }, asSuperAdmin: true), ct);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return (body.GetProperty("tenantId").GetGuid(), owner, slug);
    }

    private static HttpRequestMessage Req(
        HttpMethod method, string url, Guid sub, string? slug, object? body = null, bool asSuperAdmin = false)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (asSuperAdmin) req.Headers.Add("X-Test-Roles", "SuperAdmin");
        if (slug is not null) req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }
}
