using System.Net;
using System.Security.Cryptography;
using Dcms.Shared.Jobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Dcms.AdminApi.Media;

/// <param name="ProtectedToken">The picker's access token, encrypted with <see cref="DriveFileJob.TokenPurpose"/>.</param>
public sealed record DriveFilePayload(string FileId, string Name, Guid? FolderId, string ProtectedToken, Guid? UploadedBy);

/// <summary>
/// One file of a Google Drive import (ADR 0024), run by jobs-worker: fetch the metadata, download
/// (or export, for Docs, Sheets, Slides and Drawings) with the bar following the bytes, then
/// ingest it into the library -- whose conversion job lands in the same group, so the import reads
/// as download, then conversion, in one place.
///
/// <para>What a retry cannot fix fails at once: an expired grant, a file Drive refuses, one too
/// large or of a kind that cannot be imported. A network failure is retried.</para>
/// </summary>
public sealed class DriveFileJob(
    IOptions<GoogleDriveOptions> options,
    IHttpClientFactory httpFactory,
    MediaIngestService ingest,
    IDataProtectionProvider protection) : IJobHandler<DriveFilePayload>
{
    public const string Kind = "media.drive-file";

    /// <summary>The Data Protection purpose the token is sealed under: one key ring, both roles.</summary>
    public const string TokenPurpose = "Dcms.Jobs.DriveImport.Token.v1";

    public async Task<object?> RunAsync(DriveFilePayload payload, IJobContext context)
    {
        var ct = context.Cancellation;
        string token;
        try
        {
            token = protection.CreateProtector(TokenPurpose).Unprotect(payload.ProtectedToken);
        }
        catch (CryptographicException)
        {
            throw new JobFailedException("jobs.error.driveReauth", "The Drive grant could not be read.");
        }

        var http = httpFactory.CreateClient(AdminPluginMedia.HttpClientName);
        var api = new Uri(options.Value.ApiBase);
        var id = payload.FileId;

        await context.ReportAsync(0, null, "jobs.stages.downloading");
        using var metaResponse = await GoogleDriveImport.SendAsync(http, new Uri(api,
            $"files/{id}?fields=name,mimeType,size&supportsAllDrives=true"), token, ct);
        Refused(metaResponse.StatusCode);
        var meta = await metaResponse.Content.ReadFromJsonAsync<GoogleDriveImport.DriveFile>(ct)
                   ?? throw new JobFailedException("jobs.error.driveRefused", "Drive returned no metadata.");
        if (meta.MimeType is null)
        {
            throw new JobFailedException("jobs.error.driveRefused", "Drive returned no type.");
        }

        var export = GoogleDriveImport.ExportFor(meta.MimeType);
        if (export is null && meta.MimeType.StartsWith(GoogleDriveImport.GoogleAppsPrefix, StringComparison.Ordinal))
        {
            throw new JobFailedException("jobs.error.driveUnsupported", meta.MimeType);
        }
        // Drive's declared size is an early reject and the bar's denominator; exports have none,
        // and the real limit is what the download reads.
        if (meta.SizeBytes > MediaIngestService.MaxUploadBytes)
        {
            throw new JobFailedException("jobs.error.tooLarge");
        }
        long? total = meta.SizeBytes > 0 ? meta.SizeBytes : null;

        var source = export is { } e
            ? new Uri(api, $"files/{id}/export?mimeType={Uri.EscapeDataString(e.MimeType)}")
            : new Uri(api, $"files/{id}?alt=media&supportsAllDrives=true");
        using var download = await GoogleDriveImport.SendAsync(http, source, token, ct);
        Refused(download.StatusCode);

        await using var file = await AdminPluginMedia.DownloadCappedAsync(download, ct,
                                   read => context.ReportAsync(read, total, "jobs.stages.downloading"))
                               ?? throw new JobFailedException("jobs.error.tooLarge");

        var fileName = string.IsNullOrWhiteSpace(meta.Name) ? payload.Name : meta.Name;
        if (export is { } x && !fileName.EndsWith(x.Extension, StringComparison.OrdinalIgnoreCase))
        {
            fileName += x.Extension;
        }

        await context.ReportAsync(file.Length, file.Length, "jobs.stages.saving");
        var result = await ingest.IngestAsync(file, file.Length, fileName, payload.FolderId, payload.UploadedBy, ct,
            tenantId: context.TenantId, parentJobId: context.ParentId);
        if (!result.Ok)
        {
            throw new JobFailedException("jobs.error.ingestRefused", result.Error);
        }
        return new { assetId = result.AssetId };
    }

    private static void Refused(HttpStatusCode status)
    {
        switch (status)
        {
            case HttpStatusCode.Unauthorized:
                throw new JobFailedException("jobs.error.driveReauth");
            case HttpStatusCode.Forbidden or HttpStatusCode.NotFound:
                throw new JobFailedException("jobs.error.driveRefused", $"Drive answered {(int)status}.");
            case >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError:
                throw new JobFailedException("jobs.error.driveRefused", $"Drive answered {(int)status}.");
            case >= HttpStatusCode.InternalServerError:
                // Drive's own trouble: worth another attempt.
                throw new HttpRequestException($"Drive answered {(int)status}.");
        }
    }
}
