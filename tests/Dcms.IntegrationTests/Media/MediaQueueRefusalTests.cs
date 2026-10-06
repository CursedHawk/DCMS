using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Messaging;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dcms.IntegrationTests.Media;

/// <summary>
/// 2026-10-06: JetStream was full and refused every publish. Each Drive import stored its file,
/// committed its row, then failed to queue the processing job — a 500 for the caller, a row stuck
/// in Processing for good, and a "media.imported: success" audit record. A refused job now takes
/// the row with it, and the record says what happened.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class MediaQueueRefusalTests(AdminApiFixture fixture)
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [DockerFact]
    public async Task A_refused_processing_job_leaves_no_asset_and_no_success_on_record()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IEventPublisher>(sp =>
                new RefusesMediaJobs(ActivatorUtilities.CreateInstance<NatsEventPublisher>(sp)))));
        var client = factory.CreateClient();
        var (tenantId, owner, slug) = await TenantAsync(client, ct);

        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Png);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "dot.png");
        var req = Req(HttpMethod.Post, "/api/admin/media", owner, slug);
        req.Content = form;
        var res = await client.SendAsync(req, ct);

        res.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await ScalarAsync("""SELECT count(*) FROM media.media_assets WHERE "TenantId" = @t""", tenantId, ct))
            .Should().Be(0, "the row rolls back with the job that could not be queued");

        // Records reach audit_events through the outbox dispatcher, so wait for the failure first.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (await OutcomesAsync(tenantId, ct) is { Count: 0 } && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, ct);
        }
        (await OutcomesAsync(tenantId, ct)).Should().Equal("failure");
    }

    private async Task<List<string>> OutcomesAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """SELECT "Outcome" FROM audit.audit_events WHERE "TenantId" = @t AND "Action" = 'media.uploaded' ORDER BY "Seq" """, conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        var outcomes = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) outcomes.Add(reader.GetString(0));
        return outcomes;
    }

    private async Task<long> ScalarAsync(string sql, Guid tenantId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>JetStream as it was that day for media jobs; everything else still publishes.</summary>
    private sealed class RefusesMediaJobs(IEventPublisher inner) : IEventPublisher
    {
        public ValueTask PublishAsync<T>(string subject, T @event, CancellationToken ct = default, string? messageId = null)
            where T : IDcmsEvent =>
            subject.StartsWith("media.process.", StringComparison.Ordinal)
                ? throw new InvalidOperationException("insufficient resources")
                : inner.PublishAsync(subject, @event, ct, messageId);
    }

    private static async Task<(Guid TenantId, Guid Owner, string Slug)> TenantAsync(HttpClient client, CancellationToken ct)
    {
        var owner = Guid.NewGuid();
        var slug = "mq-" + Guid.NewGuid().ToString("N")[..8];
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
