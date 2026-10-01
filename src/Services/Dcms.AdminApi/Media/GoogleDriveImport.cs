using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dcms.AdminApi.Media;

/// <summary>
/// The platform's Google Cloud app, as the Drive picker in the console needs it. None of it is
/// secret — the picker runs in the browser — but it is per-environment, so it lives in Vault
/// (<c>secret/dcms/admin-api</c>, <c>GoogleDrive__*</c>) like every other value that differs
/// between dev and prod. Optional: unset, the console simply does not offer Drive.
/// </summary>
public sealed class GoogleDriveOptions
{
    public const string SectionName = "GoogleDrive";

    /// <summary>OAuth client id. May be the identity SSO client, with the console origin added
    /// to its authorized JavaScript origins.</summary>
    public string? ClientId { get; set; }

    /// <summary>Browser API key, restricted to the Picker API and the console's referrers.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The Cloud project number. A drive.file grant made in the picker belongs to this
    /// project, which is what lets admin-api download the picked file with the same token.</summary>
    public string? AppId { get; set; }

    /// <summary>Overridden only by tests, which point it at a stub on loopback.</summary>
    public string ApiBase { get; set; } = "https://www.googleapis.com/drive/v3/";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(AppId);
}

/// <summary>
/// Import from Google Drive: the console's picker hands over a short-lived <c>drive.file</c>
/// token and the id of a file the admin just picked; this downloads it and puts it through
/// <see cref="MediaIngestService"/>. That is the whole design — the same seam as an upload, so a
/// Drive image gets the same sniffing, sanitising and webp ladder, and no second "just store it"
/// path exists to drift. The token is used for this one request and never stored or logged.
///
/// <para>One file per request, as the uploader sends one file per request: the console's
/// per-file progress list stays honest and no request runs for minutes.</para>
/// </summary>
public static partial class GoogleDriveImport
{
    private const string GoogleAppsPrefix = "application/vnd.google-apps.";

    /// <summary>
    /// Google-native files have no bytes of their own, so Drive exports them. PDF is a type
    /// the library already stores; a Drawing goes to PNG so it gets the webp ladder like any
    /// image. Null for anything else Google-native (folders, forms, sites…), which cannot be
    /// imported.
    /// </summary>
    public static (string MimeType, string Extension)? ExportFor(string mimeType) => mimeType switch
    {
        "application/vnd.google-apps.document"
            or "application/vnd.google-apps.spreadsheet"
            or "application/vnd.google-apps.presentation" => ("application/pdf", ".pdf"),
        "application/vnd.google-apps.drawing" => ("image/png", ".png"),
        _ => null,
    };

    // Drive ids are URL-safe base64-ish. Checked because the id is pasted into a request path.
    [GeneratedRegex("^[A-Za-z0-9_-]{1,200}$")]
    private static partial Regex FileIdPattern();

    public static IEndpointRouteBuilder MapGoogleDriveImport(this IEndpointRouteBuilder app)
    {
        // 404 rather than an empty object: "not configured" hides the button, and a 404 is what
        // the console already treats as "this does not exist here".
        app.MapGet("/api/admin/media/google-drive/config", (IOptions<GoogleDriveOptions> options) =>
        {
            var o = options.Value;
            return o.IsConfigured
                ? Results.Ok(new { clientId = o.ClientId, apiKey = o.ApiKey, appId = o.AppId })
                : Results.NotFound();
        }).RequirePermission(PlatformPermissions.MediaWrite);

        app.MapPost("/api/admin/media/google-drive/import", async (
            ImportRequest body, IOptions<GoogleDriveOptions> options, IHttpClientFactory httpFactory,
            MediaIngestService ingest, MediaDbContext db, CurrentUser me,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (!options.Value.IsConfigured) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.AccessToken)
                || body.FileId is null || !FileIdPattern().IsMatch(body.FileId))
            {
                return Results.BadRequest(new { error = "A Drive file id and access token are required." });
            }
            if (body.FolderId is { } fid && !await db.Folders.AnyAsync(f => f.Id == fid, ct))
            {
                return Results.BadRequest(new { error = "Unknown folder." });
            }

            // The public-egress client: the host is fixed, but its 2-minute bound and its
            // refusal of internal addresses cost nothing to share.
            var http = httpFactory.CreateClient(AdminPluginMedia.HttpClientName);
            var api = new Uri(options.Value.ApiBase);
            var id = body.FileId;

            try
            {
                using var metaResponse = await SendAsync(http, new Uri(api,
                    $"files/{id}?fields=name,mimeType,size&supportsAllDrives=true"), body.AccessToken, ct);
                if (!metaResponse.IsSuccessStatusCode) return DriveRefused(metaResponse.StatusCode);

                var meta = await metaResponse.Content.ReadFromJsonAsync<DriveFile>(ct);
                if (meta?.MimeType is null) return DriveRefused(HttpStatusCode.NotFound);

                var export = ExportFor(meta.MimeType);
                if (export is null && meta.MimeType.StartsWith(GoogleAppsPrefix, StringComparison.Ordinal))
                {
                    return Results.BadRequest(new { error = "This kind of Google file cannot be imported." });
                }
                // Drive's declared size is only an early reject (exports have none); the real
                // limit is what ReadCappedAsync reads.
                if (meta.SizeBytes > MediaIngestService.MaxInlineBytes)
                {
                    return Results.BadRequest(new { error = "File exceeds the 50 MB inline upload limit." });
                }

                var source = export is { } e
                    ? new Uri(api, $"files/{id}/export?mimeType={Uri.EscapeDataString(e.MimeType)}")
                    : new Uri(api, $"files/{id}?alt=media&supportsAllDrives=true");
                using var download = await SendAsync(http, source, body.AccessToken, ct);
                if (!download.IsSuccessStatusCode) return DriveRefused(download.StatusCode);

                var bytes = await AdminPluginMedia.ReadCappedAsync(download, ct);
                if (bytes.Length == 0)
                {
                    return Results.BadRequest(new { error = "The file is empty or exceeds the 50 MB inline upload limit." });
                }

                var fileName = string.IsNullOrWhiteSpace(meta.Name) ? id : meta.Name;
                if (export is { } x && !fileName.EndsWith(x.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    fileName += x.Extension;
                }

                var result = await ingest.IngestAsync(bytes, fileName, body.FolderId, me.UserId, ct);
                if (!result.Ok) return Results.BadRequest(new { error = result.Error });

                // Same body as an upload, so the console treats both identically.
                return Results.Created($"/api/admin/media/{result.AssetId}", new
                {
                    id = result.AssetId,
                    category = result.Category.ToString(),
                    contentType = result.ContentType,
                    status = result.Status.ToString(),
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // The id and the exception, never the token.
                loggers.CreateLogger(typeof(GoogleDriveImport)).LogWarning(ex, "Google Drive import of {FileId} failed.", id);
                return Results.BadRequest(new { error = "Google Drive could not be reached." });
            }
        }).RequirePermission(PlatformPermissions.MediaWrite).WithAudit(AuditActions.MediaImported, "media_asset");

        return app;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient http, Uri uri, string token, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
    /// Google's status, translated. 401 is the one the console acts on — the picker's token
    /// expired — so it gets its own message; the rest are "you can't have that file".
    /// </summary>
    private static IResult DriveRefused(HttpStatusCode status) => Results.BadRequest(new
    {
        error = status == HttpStatusCode.Unauthorized
            ? "Google Drive sign-in expired. Open the Drive picker again."
            : $"Google Drive refused the file ({(int)status}).",
    });

    private sealed record ImportRequest(string? AccessToken, string? FileId, Guid? FolderId);

    private sealed record DriveFile(string? Name, string? MimeType, string? Size)
    {
        /// <summary>Drive sends size as a string, and omits it for Google-native files.</summary>
        public long SizeBytes => long.TryParse(Size, out var n) ? n : 0;
    }
}
