using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Rls;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dcms.IntegrationTests.Media;

/// <summary>
/// Drive import end to end against a stub Drive: the picked file has to land in the library
/// through the same ingest as an upload — an image Processing (so the worker builds its webp
/// ladder), a Google Doc exported to a Ready PDF — and Google's refusals must come back as
/// something the console can show, never as a 401 the edge would read as "signed out".
/// </summary>
[Collection(AdminApiCollection.Name)]
public class GoogleDriveImportTests(AdminApiFixture fixture)
{
    [DockerFact]
    public async Task A_picked_image_goes_through_ingest_like_an_upload()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (owner, slug) = await TenantAsync(client, ct);

        var config = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/media/google-drive/config", owner, slug), ct);
        config.EnsureSuccessStatusCode();
        (await config.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("appId").GetString().Should().Be("test-app");

        var res = await ImportAsync(client, owner, slug, "png1", DriveStubServer.GoodToken, ct);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("category").GetString().Should().Be("Image");
        // Processing is the claim: the worker, not this request, sanitises and builds the ladder.
        body.GetProperty("status").GetString().Should().Be("Processing");
        (await FileNameAsync(body.GetProperty("id").GetGuid(), ct)).Should().Be("photo.png");
    }

    [DockerFact]
    public async Task A_google_doc_is_exported_to_pdf()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (owner, slug) = await TenantAsync(client, ct);

        var res = await ImportAsync(client, owner, slug, "doc1", DriveStubServer.GoodToken, ct);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("contentType").GetString().Should().Be("application/pdf");
        body.GetProperty("status").GetString().Should().Be("Ready");
        (await FileNameAsync(body.GetProperty("id").GetGuid(), ct)).Should().Be("Brochure.pdf");
        fixture.DriveStub.Requests.Should().Contain("/drive/v3/files/doc1/export?mimeType=application%2Fpdf");
    }

    [DockerFact]
    public async Task An_expired_token_is_a_400_the_console_can_show_not_a_401()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (owner, slug) = await TenantAsync(client, ct);

        var res = await ImportAsync(client, owner, slug, "png1", "expired", ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(ct)).Should().Contain("sign-in expired");
    }

    [DockerFact]
    public async Task An_oversized_file_is_refused_before_it_is_downloaded()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (owner, slug) = await TenantAsync(client, ct);

        var res = await ImportAsync(client, owner, slug, "big1", DriveStubServer.GoodToken, ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fixture.DriveStub.Requests.Should().NotContain(p => p.StartsWith("/drive/v3/files/big1?alt=media"));
    }

    private static Task<HttpResponseMessage> ImportAsync(
        HttpClient client, Guid owner, string slug, string fileId, string token, CancellationToken ct) =>
        client.SendAsync(Req(HttpMethod.Post, "/api/admin/media/google-drive/import", owner, slug,
            new { accessToken = token, fileId }), ct);

    private async Task<string> FileNameAsync(Guid assetId, CancellationToken ct)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var media = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
        using var rls = RlsScope.Platform();
        return await media.Assets.IgnoreQueryFilters().Where(a => a.Id == assetId).Select(a => a.FileName).SingleAsync(ct);
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
}

/// <summary>
/// A stand-in for the Drive v3 API, in-process on loopback. Three files: a PNG, a Google Doc
/// (exportable only) and one whose metadata claims 60 MB. Anything but <see cref="GoodToken"/>
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
            ("big1", _) => Results.Json(new { name = "huge.mp4", mimeType = "video/mp4", size = (60L * 1024 * 1024).ToString() }),
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
