using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml;
using Microsoft.Extensions.Options;

namespace Dcms.Plugins.YouTube;

/// <summary>
/// Platform-level YouTube configuration, bound from the <c>YouTube</c> section (Vault:
/// <c>YouTube__ApiKey</c>). Leave the key out and the plugin does not offer itself: the sync
/// does nothing and the settings say the deployment has no YouTube access — the Google SSO and
/// Meta convention, so dev needs no Google project.
///
/// <para>The key is a <b>server</b> key: restricted to the YouTube Data API v3, not to HTTP
/// referrers. A referrer-restricted browser key (like the Drive picker's) is refused here.</para>
/// </summary>
public sealed class YouTubeOptions
{
    public const string SectionName = "YouTube";

    public string? ApiKey { get; set; }

    /// <summary>
    /// Replaces <c>https://www.googleapis.com</c>. <b>For tests only</b>, the same rule as the
    /// Meta plugins' override: without it the sync could only ever be exercised against Google.
    /// </summary>
    public string? OverrideBaseUrl { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>A Data API error. <see cref="Reason"/> is Google's, e.g. <c>quotaExceeded</c> or <c>playlistNotFound</c>.</summary>
public sealed class YouTubeApiException(HttpStatusCode status, string? reason, string? message)
    : Exception(message ?? $"YouTube answered {(int)status}{(reason is null ? "" : $" ({reason})")}.")
{
    public HttpStatusCode Status { get; } = status;

    public string? Reason { get; } = reason;

    public bool IsQuota => Reason is "quotaExceeded" or "rateLimitExceeded" or "dailyLimitExceeded";
}

public sealed record ChannelInfo(string Id, string Title, string? Handle, string? AvatarUrl, string UploadsPlaylist);

public sealed record PlaylistInfo(string Id, string Title, string? ChannelId, string? ChannelTitle);

public sealed record VideoInfo(
    string Id,
    string Title,
    string Description,
    DateTimeOffset? PublishedAt,
    string? ChannelTitle,
    string? Thumbnail,
    int? DurationSeconds,
    long? ViewCount,
    long? LikeCount,
    bool Live);

/// <summary>
/// The four Data API v3 reads the plugin makes. Each costs one quota unit per call, and a call
/// carries at most 50 ids or returns at most 50 items — the API's own page size.
/// </summary>
public sealed class YouTubeClient(HttpClient http, IOptions<YouTubeOptions> options)
{
    public const int PageSize = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => options.Value.IsConfigured;

    public async Task<ChannelInfo?> ChannelAsync(ChannelRef channel, CancellationToken ct)
    {
        var filter = channel.Kind switch
        {
            ChannelRefKind.Id => $"id={Uri.EscapeDataString(channel.Value)}",
            ChannelRefKind.Handle => $"forHandle={Uri.EscapeDataString("@" + channel.Value)}",
            _ => $"forUsername={Uri.EscapeDataString(channel.Value)}",
        };
        var page = await GetAsync<ItemList<ChannelItem>>($"channels?part=snippet,contentDetails&{filter}", ct);
        return page.Items?.FirstOrDefault() is { } c
            ? new ChannelInfo(
                c.Id, c.Snippet?.Title ?? c.Id, c.Snippet?.CustomUrl, BestThumbnail(c.Snippet?.Thumbnails),
                c.ContentDetails?.RelatedPlaylists?.Uploads ?? YouTubeSource.UploadsPlaylist(c.Id))
            : null;
    }

    public async Task<PlaylistInfo?> PlaylistAsync(string playlistId, CancellationToken ct)
    {
        var page = await GetAsync<ItemList<PlaylistItem>>($"playlists?part=snippet&id={Uri.EscapeDataString(playlistId)}", ct);
        return page.Items?.FirstOrDefault() is { } p
            ? new PlaylistInfo(p.Id, p.Snippet?.Title ?? p.Id, p.Snippet?.ChannelId, p.Snippet?.ChannelTitle)
            : null;
    }

    /// <summary>One page of a playlist's video ids, in playlist order.</summary>
    public async Task<(IReadOnlyList<string> VideoIds, string? NextPageToken)> PlaylistPageAsync(
        string playlistId, string? pageToken, CancellationToken ct)
    {
        var url = $"playlistItems?part=contentDetails&maxResults={PageSize}&playlistId={Uri.EscapeDataString(playlistId)}"
                  + (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
        var page = await GetAsync<ItemList<PlaylistEntry>>(url, ct);
        var ids = (page.Items ?? [])
            .Select(i => i.ContentDetails?.VideoId)
            .OfType<string>()
            .ToList();
        return (ids, page.NextPageToken);
    }

    /// <summary>
    /// The videos among <paramref name="ids"/> that are public. Private and deleted videos are
    /// simply absent from the answer, which is how a playlist's dead entries drop out.
    /// </summary>
    public async Task<IReadOnlyList<VideoInfo>> VideosAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var page = await GetAsync<ItemList<VideoItem>>(
            "videos?part=snippet,contentDetails,statistics,liveStreamingDetails&id="
            + Uri.EscapeDataString(string.Join(',', ids.Take(PageSize))), ct);

        return (page.Items ?? []).Select(v => new VideoInfo(
            v.Id,
            v.Snippet?.Title ?? string.Empty,
            v.Snippet?.Description ?? string.Empty,
            v.Snippet?.PublishedAt,
            v.Snippet?.ChannelTitle,
            BestThumbnail(v.Snippet?.Thumbnails) ?? $"https://i.ytimg.com/vi/{v.Id}/hqdefault.jpg",
            DurationSeconds(v.ContentDetails?.Duration),
            v.Statistics?.ViewCount,
            v.Statistics?.LikeCount,
            Live: v.LiveStreamingDetails is { ValueKind: JsonValueKind.Object }
                  || v.Snippet?.LiveBroadcastContent is "live" or "upcoming")).ToList();
    }

    /// <summary>An ISO-8601 duration (<c>PT4M13S</c>) in seconds; null for none or nonsense.</summary>
    public static int? DurationSeconds(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        try
        {
            return (int)XmlConvert.ToTimeSpan(iso).TotalSeconds;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task<T> GetAsync<T>(string pathAndQuery, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.IsConfigured) throw new InvalidOperationException("YouTube is not configured on this deployment.");
        var baseUrl = (o.OverrideBaseUrl ?? "https://www.googleapis.com").TrimEnd('/');

        using var res = await http.GetAsync($"{baseUrl}/youtube/v3/{pathAndQuery}&key={Uri.EscapeDataString(o.ApiKey!)}", ct);
        if (!res.IsSuccessStatusCode)
        {
            ErrorBody? body = null;
            try
            {
                body = await res.Content.ReadFromJsonAsync<ErrorBody>(Json, ct);
            }
            catch (JsonException)
            {
                // Not Google's error shape (a proxy, an outage page); the status says enough.
            }
            throw new YouTubeApiException(res.StatusCode, body?.Error?.Errors?.FirstOrDefault()?.Reason, body?.Error?.Message);
        }
        return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private static string? BestThumbnail(Thumbnails? t) =>
        (t?.Maxres ?? t?.Standard ?? t?.High ?? t?.Medium ?? t?.Default)?.Url;

    // ---------- wire shapes: only the fields read above ----------

    private sealed record ItemList<T>(List<T>? Items, string? NextPageToken);

    private sealed record Thumbnail(string? Url);

    private sealed record Thumbnails(Thumbnail? Default, Thumbnail? Medium, Thumbnail? High, Thumbnail? Standard, Thumbnail? Maxres);

    private sealed record ChannelSnippet(string? Title, string? CustomUrl, Thumbnails? Thumbnails);

    private sealed record RelatedPlaylists(string? Uploads);

    private sealed record ChannelContent(RelatedPlaylists? RelatedPlaylists);

    private sealed record ChannelItem(string Id, ChannelSnippet? Snippet, ChannelContent? ContentDetails);

    private sealed record PlaylistSnippet(string? Title, string? ChannelId, string? ChannelTitle);

    private sealed record PlaylistItem(string Id, PlaylistSnippet? Snippet);

    private sealed record EntryContent(string? VideoId);

    private sealed record PlaylistEntry(EntryContent? ContentDetails);

    private sealed record VideoSnippet(
        string? Title, string? Description, DateTimeOffset? PublishedAt, string? ChannelTitle,
        Thumbnails? Thumbnails, string? LiveBroadcastContent);

    private sealed record VideoContent(string? Duration);

    private sealed record VideoStatistics(long? ViewCount, long? LikeCount);

    private sealed record VideoItem(
        string Id, VideoSnippet? Snippet, VideoContent? ContentDetails, VideoStatistics? Statistics,
        JsonElement? LiveStreamingDetails);

    private sealed record ErrorReason(string? Reason);

    private sealed record ErrorDetail(string? Message, List<ErrorReason>? Errors);

    private sealed record ErrorBody(ErrorDetail? Error);
}
