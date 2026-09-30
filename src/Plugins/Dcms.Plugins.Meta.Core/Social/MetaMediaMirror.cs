using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.Meta.Core;

/// <summary>
/// Copies a Meta CDN file into tenant media, once.
///
/// <para>Mirroring is not an optimisation. Meta's media URLs are signed and expire within
/// days, so a site that stored one would show a working image in review and broken images a
/// week later — the failure arrives long after the change that caused it.</para>
///
/// <para>The copy goes through <c>dcms.media@1</c>'s import, so bytes fetched from a third
/// party get exactly the sniffing, sanitising and size limit an admin's own upload gets.
/// These are the bytes that need it most.</para>
/// </summary>
public sealed class MetaMediaMirror(SocialDbContext social, ILogger<MetaMediaMirror> logger)
{
    /// <summary>
    /// Returns the mirrored asset id for a Meta media URL, downloading it only the first time.
    ///
    /// <para>Returns null when there is nothing to mirror or the import failed — a missing
    /// picture must not fail the whole post, because one unreachable CDN file would otherwise
    /// stall a tenant's entire feed.</para>
    /// </summary>
    public async Task<Guid?> MirrorAsync(
        IPluginMedia media,
        Guid tenantId,
        Guid connectionId,
        string externalMediaId,
        string? url,
        CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(tenantId); // ADR 0015: mirrors into this tenant's media only.
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var source))
        {
            return null;
        }

        var existing = await social.MediaMap.IgnoreQueryFilters().FirstOrDefaultAsync(
            m => m.TenantId == tenantId
                 && m.ConnectionId == connectionId
                 && m.ExternalMediaId == externalMediaId, ct);

        // The whole reason a 15-minute poll is affordable: the second pass over the same post
        // does no network and no image processing at all.
        if (existing is not null) return existing.MediaAssetId;

        var result = await media.ImportAsync(new MediaImport(source, externalMediaId), ct);
        if (result.AssetId is not { } assetId)
        {
            logger.LogWarning("Meta media {MediaId} was not mirrored: {Error}", externalMediaId, result.Error);
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
}
