extern alias AdminApiApp;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Rls;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dcms.IntegrationTests.Media;

/// <summary>
/// Drive import end to end against a stub Drive (ADR 0024): the request queues the picked files
/// and returns at once; a job worker downloads each one and lands it in the library through the
/// same ingest as an upload -- an image Processing (its conversion joins the import's group), a
/// Google Doc exported to a Ready PDF -- and Google's refusals end the file's job with a reason the
/// console can show, not with retries.
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class GoogleDriveImportTests(AdminApiFixture fixture) : IAsyncDisposable
{
    private WebApplicationFactory<AdminApiApp::Program>? _host;

    /// <summary>One process that both accepts the import and runs it: the "all" role, lane on.</summary>
    private WebApplicationFactory<AdminApiApp::Program> Host =>
        _host ??= fixture.Factory.WithWebHostBuilder(b => b.UseSetting("Jobs:Lanes:tenant:Enabled", "true"));

    [DockerFact]
    public async Task A_picked_image_goes_through_ingest_like_an_upload_and_its_conversion_joins_the_import()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Host.CreateClient();
        var (owner, slug) = await TenantAsync(client, ct);

        var config = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/media/google-drive/config", owner, slug), ct);
        config.EnsureSuccessStatusCode();
        (await config.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("appId").GetString().Should().Be("test-app");

        var group = await ImportAsync(client, owner, slug, "png1", DriveStubServer.GoodToken, ct);
        var (file, detail) = await FileJobAsync(client, owner, slug, group, ct);

        file.GetProperty("status").GetString().Should().Be("Succeeded");
        var assetId = file.GetProperty("result").GetProperty("assetId").GetGuid();
        // Processing is the claim: media-worker, not this job, sanitises and builds the ladder.
        (await AssetAsync(assetId, ct)).Should().Be(("photo.png", MediaStatus.Processing));
        detail.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("kind").GetString())
            .Should().Contain("media.process.image", "the conversion is shown under the import that caused it");
    }

    [DockerFact]
    public async Task A_google_doc_is_exported_to_pdf()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Host.CreateClient();
        var (owner, slug) = await TenantAsync(client, ct);

        var group = await ImportAsync(client, owner, slug, "doc1", DriveStubServer.GoodToken, ct);
        var (file, _) = await FileJobAsync(client, owner, slug, group, ct);

        file.GetProperty("status").GetString().Should().Be("Succeeded");
        (await AssetAsync(file.GetProperty("result").GetProperty("assetId").GetGuid(), ct))
            .Should().Be(("Brochure.pdf", MediaStatus.Ready));
        fixture.DriveStub.Requests.Should().Contain("/drive/v3/files/doc1/export?mimeType=application%2Fpdf");
    }

    [DockerFact]
    public async Task An_expired_token_fails_the_file_once_with_a_reason_the_console_can_show()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Host.CreateClient();
        var (owner, slug) = await TenantAsync(client, ct);

        var group = await ImportAsync(client, owner, slug, "png1", "expired", ct);
        var (file, _) = await FileJobAsync(client, owner, slug, group, ct);

        file.GetProperty("status").GetString().Should().Be("Failed");
        file.GetProperty("errorKey").GetString().Should().Be("jobs.error.driveReauth");
        file.GetProperty("attempts").GetInt32().Should().Be(1, "retrying cannot renew somebody's Google sign-in");
    }

    [DockerFact]
    public async Task An_oversized_file_is_refused_before_it_is_downloaded()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Host.CreateClient();
        var (owner, slug) = await TenantAsync(client, ct);

        var group = await ImportAsync(client, owner, slug, "big1", DriveStubServer.GoodToken, ct);
        var (file, _) = await FileJobAsync(client, owner, slug, group, ct);

        file.GetProperty("errorKey").GetString().Should().Be("jobs.error.tooLarge");
        fixture.DriveStub.Requests.Should().NotContain(p => p.StartsWith("/drive/v3/files/big1?alt=media"));
    }

    private static async Task<Guid> ImportAsync(
        HttpClient client, Guid owner, string slug, string fileId, string token, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/media/google-drive/import", owner, slug,
            new { accessToken = token, files = new[] { new { id = fileId, name = fileId } } }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Accepted, "the import is queued, not performed in the request");
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("jobId").GetGuid();
    }

    /// <summary>The import's one file job, once it has finished, and the group's detail.</summary>
    private static async Task<(JsonElement File, JsonElement Detail)> FileJobAsync(
        HttpClient client, Guid owner, string slug, Guid group, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var res = await client.SendAsync(Req(HttpMethod.Get, $"/api/admin/jobs/{group}", owner, slug), ct);
            res.EnsureSuccessStatusCode();
            var detail = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
            var file = detail.GetProperty("children").EnumerateArray()
                .FirstOrDefault(c => c.GetProperty("kind").GetString() == "media.drive-file");
            if (file.ValueKind == JsonValueKind.Object
                && file.GetProperty("status").GetString() is "Succeeded" or "Failed" or "Canceled")
            {
                return (file, detail);
            }
            await Task.Delay(200, ct);
        }
        throw new TimeoutException($"The Drive file job under {group} never finished.");
    }

    private async Task<(string FileName, MediaStatus Status)> AssetAsync(Guid assetId, CancellationToken ct)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var media = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
        using var rls = RlsScope.Platform();
        var asset = await media.Assets.IgnoreQueryFilters().Where(a => a.Id == assetId)
            .Select(a => new { a.FileName, a.Status }).SingleAsync(ct);
        return (asset.FileName, asset.Status);
    }

    private static async Task<(Guid Owner, string Slug)> TenantAsync(HttpClient client, CancellationToken ct)
    {
        var owner = Guid.NewGuid();
        var slug = "drive-" + Guid.NewGuid().ToString("N")[..8];
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", owner, null,
            new { slug, name = slug, ownerUserId = owner, ownerEmail = "o@dcms.test" }, asSuperAdmin: true), ct);
        res.EnsureSuccessStatusCode();
        return (owner, slug);
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

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }
}

/// <summary>
/// A stand-in for the Drive v3 API, in-process on loopback. Three files: a PNG, a Google Doc
/// (exportable only) and one whose metadata claims 2 GB. Anything but <see cref="GoodToken"/>
/// gets 401, as an expired picker token does.
/// </summary>
public sealed class DriveStubServer : IAsyncDisposable
{
    public const string GoodToken = "good-token";

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static readonly byte[] Pdf = "%PDF-1.4\n%%EOF\n"u8.ToArray();

    private readonly WebApplication _app;

    private DriveStubServer(WebApplication app) => _app = app;

    /// <summary>What admin-api is configured with as GoogleDrive:ApiBase.</summary>
    public string ApiBase { get; private set; } = string.Empty;

    /// <summary>Every path and query asked for, across all tests.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    public static async Task<DriveStubServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        var stub = new DriveStubServer(app);

        app.Use(async (http, next) =>
        {
            stub.Requests.Enqueue(http.Request.Path.Value + http.Request.QueryString.Value);
            if (http.Request.Headers.Authorization != $"Bearer {GoodToken}")
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next();
        });

        app.MapGet("/drive/v3/files/{id}", (string id, string? alt) => (id, alt) switch
        {
            ("png1", "media") => Results.Bytes(Png, "image/png"),
            ("png1", _) => Results.Json(new { name = "photo.png", mimeType = "image/png", size = Png.Length.ToString() }),
            ("doc1", _) => Results.Json(new { name = "Brochure", mimeType = "application/vnd.google-apps.document" }),
            ("big1", _) => Results.Json(new { name = "huge.mp4", mimeType = "video/mp4", size = (2L * 1024 * 1024 * 1024).ToString() }),
            _ => Results.NotFound(),
        });
        app.MapGet("/drive/v3/files/{id}/export", (string id, string mimeType) =>
            id == "doc1" && mimeType == "application/pdf" ? Results.Bytes(Pdf, "application/pdf") : Results.NotFound());

        await app.StartAsync();
        stub.ApiBase = app.Urls.First() + "/drive/v3/";
        return stub;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
