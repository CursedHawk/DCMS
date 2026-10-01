using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime.Platform;
using Dcms.Shared.Data.Media;

namespace Dcms.AdminApi.Media;

/// <summary>
/// <c>dcms.media@1</c> on the admin plane: resolving as everywhere, plus importing a remote file
/// through <see cref="MediaIngestService"/> — the same sniffing, sanitising and size limit an
/// admin's own upload gets. Third-party bytes are the ones that need it most, which is why a
/// plugin gets this rather than a way to write to storage.
/// </summary>
public sealed class AdminPluginMedia(
    IPluginContext caller,
    MediaDbContext db,
    MediaIngestService ingest,
    IHttpClientFactory httpFactory,
    IConfiguration configuration,
    ILogger<AdminPluginMedia> logger) : PluginMedia(caller, db)
{
    /// <summary>Named so its timeout lives in one place: files, not JSON, but bounded.</summary>
    public const string HttpClientName = "plugin-media-import";

    public override async Task<MediaImportResult> ImportAsync(MediaImport input, CancellationToken ct)
    {
        // https to a public address only. The URL typically comes from a third-party API, so it
        // must not be able to point the platform at the internal network: the scheme is checked
        // here, and every connection's address (redirects included) by the client's PublicEgress
        // handler. Tests, whose stub CDN is plain http on loopback, set Media:ImportAllowLocal.
        var allowLocal = configuration.GetValue("Media:ImportAllowLocal", false);
        if (!input.Url.IsAbsoluteUri
            || !(input.Url.Scheme == Uri.UriSchemeHttps || (allowLocal && input.Url.Scheme == Uri.UriSchemeHttp))
            || input.Url.UserInfo.Length > 0)
        {
            throw new ContractValidationException("Only https URLs without credentials can be imported.");
        }

        byte[] bytes;
        try
        {
            using var response = await httpFactory.CreateClient(HttpClientName)
                .GetAsync(input.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new MediaImportResult(null, $"The source returned {(int)response.StatusCode}.");
            }
            // The declared length is only an early reject; the real limit is what is read,
            // because Content-Length is the server's claim.
            if (response.Content.Headers.ContentLength is > MediaIngestService.MaxInlineBytes)
            {
                return new MediaImportResult(null, "The file exceeds the inline size limit.");
            }
            bytes = await ReadCappedAsync(response, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Plugin {Plugin} could not import {Host}.", Caller.PluginId, input.Url.Host);
            return new MediaImportResult(null, "The file could not be downloaded.");
        }
        if (bytes.Length == 0)
        {
            return new MediaImportResult(null, "The file is empty or exceeds the inline size limit.");
        }

        var result = await ingest.IngestAsync(bytes, input.FileName, input.FolderId, createdBy: null, ct, tenantId: Caller.TenantId);
        return result.Ok ? new MediaImportResult(result.AssetId, null) : new MediaImportResult(null, result.Error);
    }

    /// <summary>Reads at most <see cref="MediaIngestService.MaxInlineBytes"/>; empty when the body is longer. Shared with the Drive import.</summary>
    internal static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MediaIngestService.MaxInlineBytes)
            {
                return [];
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
