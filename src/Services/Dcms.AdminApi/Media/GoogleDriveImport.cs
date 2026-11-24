using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Jobs;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Jobs;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dcms.AdminApi.Media;

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
        }).RequirePermission(PlatformPermissions.MediaWrite)
          .NotForCli("Google Drive files are picked in the browser, with the person signed in to Google.");

        // Queues the picked files and returns (ADR 0024). Each file is a job the worker runs --
        // download, ingest, then the media conversion under the same group -- so the import goes
        // on whether or not the page that started it stays open. The token travels encrypted:
        // it is a credential to the person's Drive, and it sits on the ledger until retention.
        // The jobs require media:write, as this endpoint does: retrying one re-downloads with
        // that person's grant, which a member who may only look must not be able to set off.
        app.MapPost("/api/admin/media/google-drive/import", async (
            ImportRequest body, IOptions<GoogleDriveOptions> options, MediaDbContext db, CurrentUser me,
            ITenantContext tenant, IJobQueue jobs, IDataProtectionProvider protection, CancellationToken ct) =>
        {
            if (!options.Value.IsConfigured) return Results.NotFound();
            var files = body.Files ?? (body.FileId is { } single ? [new PickedFile(single, null)] : []);
            if (string.IsNullOrWhiteSpace(body.AccessToken) || files.Count is 0 or > MaxFilesPerImport
                || files.Any(f => f.Id is null || !FileIdPattern().IsMatch(f.Id)))
            {
                return Results.BadRequest(new { error = $"Between 1 and {MaxFilesPerImport} Drive file ids and an access token are required." });
            }
            if (body.FolderId is { } fid && !await db.Folders.AnyAsync(f => f.Id == fid, ct))
            {
                return Results.BadRequest(new { error = "Unknown folder." });
            }

            var tenantId = tenant.TenantId!.Value;
            var token = protection.CreateProtector(DriveFileJobContract.TokenPurpose).Protect(body.AccessToken);
            var group = await jobs.EnqueueAsync(new JobSpec(
                "media.drive-import", tenantId, null, "jobs.kinds.driveImport.title", new { count = files.Count },
                RequiredPermission: PlatformPermissions.MediaWrite, Lane: JobLanes.Group, LinkPath: "/media"), ct);
            foreach (var file in files)
            {
                // The name is the picker's, for the row until the file's own arrives; it is only
                // ever displayed, to the same workspace that picked it.
                var name = string.IsNullOrWhiteSpace(file.Name) ? file.Id! : file.Name[..Math.Min(file.Name.Length, 200)];
                await jobs.EnqueueAsync(new JobSpec(
                    DriveFileJobContract.Kind, tenantId, new DriveFilePayload(file.Id!, name, body.FolderId, token, me.UserId),
                    "jobs.kinds.driveFile.title", new { name },
                    RequiredPermission: PlatformPermissions.MediaWrite, ParentId: group.Id, LinkPath: "/media"), ct);
            }
            return Results.Accepted($"/api/admin/jobs/{group.Id}", new { jobId = group.Id });
        }).RequirePermission(PlatformPermissions.MediaWrite).WithAudit(AuditActions.MediaImported, "media_asset")
          .NotForCli("Google Drive files are picked in the browser, with the person signed in to Google.");

        return app;
    }

    /// <summary>
    /// Google's status, translated. 401 is the one the console acts on — the picker's token
    /// expired — so it gets its own message; the rest are "you can't have that file".
    /// </summary>
    /// <summary>One request's ceiling: a picker selection, not a migration.</summary>
    private const int MaxFilesPerImport = 100;

    /// <param name="FileId">The single file of a request from an older console.</param>
    private sealed record ImportRequest(string? AccessToken, IReadOnlyList<PickedFile>? Files, string? FileId, Guid? FolderId);

    private sealed record PickedFile(string? Id, string? Name);

}
