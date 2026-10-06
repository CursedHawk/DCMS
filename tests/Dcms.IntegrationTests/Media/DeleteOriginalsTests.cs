using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Dcms.IntegrationTests.Media;

/// <summary>
/// A tenant frees space by deleting originals its web copies can stand in for. The asset stays,
/// "original" is answered by the copy, and the cap stops counting the bytes. Anything without a
/// copy that can stand in — still processing, video, a plain file — is skipped and counted.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class DeleteOriginalsTests(AdminApiFixture fixture)
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    private static readonly byte[] Webp = "RIFF\0\0\0\0WEBPVP8 stand-in"u8.ToArray();

    [DockerFact]
    public async Task A_processed_picture_loses_its_original_and_keeps_answering()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (tenantId, owner, slug) = await TenantAsync(client, ct);
        var ready = await UploadAsync(client, owner, slug, ct);
        var processing = await UploadAsync(client, owner, slug, ct);
        var key = await ProcessedAsync(ready, tenantId, ct);

        var before = await UsageAsync(client, owner, slug, ct);
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/media/delete-originals", owner, slug,
            new { ids = new[] { ready, processing } }), ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("deleted").GetInt32().Should().Be(1);
        body.GetProperty("skipped").GetInt32().Should().Be(1, "a picture still being processed has no copy yet");
        body.GetProperty("freedBytes").GetInt64().Should().Be(Png.Length);

        var storage = fixture.Factory.Services.GetRequiredService<IObjectStorage>();
        var bucket = fixture.Factory.Services.GetRequiredService<IOptions<StorageOptions>>().Value.MediaBucket;
        (await storage.ExistsAsync(bucket, key, ct)).Should().BeFalse();

        // "original" is now the largest WebP, so pages that link it keep their picture.
        var content = await client.SendAsync(Req(HttpMethod.Get, $"/api/admin/media/{ready}/content", owner, slug), ct);
        content.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Content.Headers.ContentType!.MediaType.Should().Be("image/webp");
        (await content.Content.ReadAsByteArrayAsync(ct)).Should().Equal(Webp);

        var after = await UsageAsync(client, owner, slug, ct);
        after.Should().Be(before - Png.Length);

        var detail = await client.SendAsync(Req(HttpMethod.Get, $"/api/admin/media/{ready}", owner, slug), ct);
        var asset = await detail.Content.ReadFromJsonAsync<JsonElement>(ct);
        asset.GetProperty("originalDeletedAt").ValueKind.Should().Be(JsonValueKind.String);
        asset.GetProperty("canDeleteOriginal").GetBoolean().Should().BeFalse("it is already gone");
    }

    [DockerFact]
    public async Task The_library_says_which_originals_can_go()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (tenantId, owner, slug) = await TenantAsync(client, ct);
        var ready = await UploadAsync(client, owner, slug, ct);
        var processing = await UploadAsync(client, owner, slug, ct);
        await ProcessedAsync(ready, tenantId, ct);

        var list = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/media", owner, slug), ct);
        var items = (await list.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetGuid(), e => e.GetProperty("canDeleteOriginal").GetBoolean());

        items[ready].Should().BeTrue();
        items[processing].Should().BeFalse();
    }

    [DockerFact]
    public async Task A_reader_may_not_delete_originals()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (_, owner, slug) = await TenantAsync(client, ct);

        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/media/delete-originals", Guid.NewGuid(), slug,
            new { ids = new[] { Guid.NewGuid() } }), ct);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>What the media worker leaves behind for a picture: Ready, with a WebP ladder.</summary>
    private async Task<string> ProcessedAsync(Guid assetId, Guid tenantId, CancellationToken ct)
    {
        var storage = fixture.Factory.Services.GetRequiredService<IObjectStorage>();
        var bucket = fixture.Factory.Services.GetRequiredService<IOptions<StorageOptions>>().Value.MediaBucket;
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);

        await using (var update = new NpgsqlCommand(
            """UPDATE media.media_assets SET "Status" = 'Ready' WHERE "Id" = @id RETURNING "OriginalKey" """, conn))
        {
            update.Parameters.AddWithValue("id", assetId);
            var key = (string)(await update.ExecuteScalarAsync(ct))!;

            foreach (var (kind, width) in new[] { ("webp-320", 320), ("webp-1280", 1280) })
            {
                var variantKey = $"tenants/{tenantId}/{assetId}/{kind}.webp";
                await storage.PutAsync(bucket, variantKey, new MemoryStream(Webp), Webp.Length, "image/webp", ct);
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO media.media_variants ("Id", "AssetId", "Kind", "ObjectKey", "ContentType", "SizeBytes", "Width", "Height", "TenantId")
                    VALUES (@vid, @id, @kind, @key, 'image/webp', @size, @w, @w, @t)
                    """, conn);
                insert.Parameters.AddWithValue("vid", Guid.NewGuid());
                insert.Parameters.AddWithValue("id", assetId);
                insert.Parameters.AddWithValue("kind", kind);
                insert.Parameters.AddWithValue("key", variantKey);
                insert.Parameters.AddWithValue("size", (long)Webp.Length);
                insert.Parameters.AddWithValue("w", width);
                insert.Parameters.AddWithValue("t", tenantId);
                await insert.ExecuteNonQueryAsync(ct);
            }
            return key;
        }
    }

    private static async Task<long> UsageAsync(HttpClient client, Guid owner, string slug, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/media/usage", owner, slug), ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("totalBytes").GetInt64();
    }

    private static async Task<Guid> UploadAsync(HttpClient client, Guid owner, string slug, CancellationToken ct)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Png);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "dot.png");
        var req = Req(HttpMethod.Post, "/api/admin/media", owner, slug);
        req.Content = form;
        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
    }

    private static async Task<(Guid TenantId, Guid Owner, string Slug)> TenantAsync(HttpClient client, CancellationToken ct)
    {
        var owner = Guid.NewGuid();
        var slug = "orig-" + Guid.NewGuid().ToString("N")[..8];
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
