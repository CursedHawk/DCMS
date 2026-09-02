using Dcms.AdminApi.Media;
using Dcms.Shared.Data.Social;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Social;

/// <summary>
/// Copies a Meta CDN file into tenant media, once.
///
/// <para>Mirroring is not an optimisation. Meta's media URLs are signed and expire within
/// days, so a site that stored one would show a working image in review and broken images a
/// week later — the failure arrives long after the change that caused it.</para>
///
/// <para>Everything goes through <see cref="MediaIngestService"/> rather than straight to
/// storage, so bytes fetched from a third party get exactly the sniffing, EXIF stripping and
/// re-encoding an admin's own upload gets. These are the bytes that need it most.</para>
/// </summary>
public sealed class MetaMediaMirror(
    IHttpClientFactory httpFactory,
    MediaIngestService ingest,
    SocialDbContext social,
    ILogger<MetaMediaMirror> logger)
{
    /// <summary>The client is named so its timeout and redirect policy are configured in one place.</summary>
    public const string HttpClientName = "meta-cdn";

    /// <summary>
    /// Returns the mirrored asset id for a Meta media URL, downloading it only the first time.
    ///
    /// <para>Returns null when there is nothing to mirror or the download failed — a missing
    /// picture must not fail the whole post, because one unreachable CDN file would otherwise
    /// stall a tenant's entire feed.</para>
    /// </summary>
    public async Task<Guid?> MirrorAsync(
        Guid tenantId,
        Guid connectionId,
        string externalMediaId,
        string? url,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var existing = await social.MediaMap.IgnoreQueryFilters().FirstOrDefaultAsync(
            m => m.TenantId == tenantId
                 && m.ConnectionId == connectionId
                 && m.ExternalMediaId == externalMediaId, ct);

        // The whole reason a 15-minute poll is affordable: the second pass over the same post
        // does no network and no image processing at all.
        if (existing is not null) return existing.MediaAssetId;

        byte[] bytes;
        try
        {
            var client = httpFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Meta CDN returned {Status} for media {MediaId}; skipping.",
                    (int)response.StatusCode, externalMediaId);
                return null;
            }

            // Trust the declared length only as an early reject; the real limit is enforced by
            // how much is actually read, because Content-Length is the server's claim.
            if (response.Content.Headers.ContentLength is > MediaIngestService.MaxInlineBytes)
            {
                logger.LogWarning("Meta media {MediaId} exceeds the inline size limit; skipping.", externalMediaId);
                return null;
            }

            bytes = await ReadCappedAsync(response, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not download Meta media {MediaId}; skipping.", externalMediaId);
            return null;
        }

        if (bytes.Length == 0) return null;

        var result = await ingest.IngestAsync(
            bytes, $"{externalMediaId}", folderId: null, createdBy: null, ct, tenantId: tenantId);

        if (!result.Ok || result.AssetId is not { } assetId)
        {
            logger.LogWarning("Meta media {MediaId} was rejected by the media pipeline: {Error}",
                externalMediaId, result.Error);
            return null;
        }

        social.MediaMap.Add(new MetaMediaMap
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            ExternalMediaId = externalMediaId,
            MediaAssetId = assetId,
            MirroredAt = DateTimeOffset.UtcNow,
        });
        await social.SaveChangesAsync(ct);

        return assetId;
    }

    /// <summary>
    /// Reads at most the inline limit, then stops. A remote server can always claim one length
    /// and send another, so the cap has to be on what is read rather than on what was promised.
    /// </summary>
    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();

        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MediaIngestService.MaxInlineBytes) return [];
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
