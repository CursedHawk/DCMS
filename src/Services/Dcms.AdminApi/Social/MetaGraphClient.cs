using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Dcms.AdminApi.Social;

/// <summary>One post, reel or story, normalised across the two providers.</summary>
public sealed record MetaFeedItem(
    string ExternalId,
    string? Caption,
    string? Permalink,
    string? MediaType,
    string? MediaProductType,
    string? MediaUrl,
    string? ThumbnailUrl,
    DateTimeOffset? PostedAt,
    string? Username,
    IReadOnlyList<MetaFeedChild> Children)
{
    /// <summary>
    /// Reels are ordinary media with <c>media_product_type = REELS</c>; there is no separate
    /// endpoint. This is the only thing separating a reel from a feed video, and it is what
    /// decides which per-type cap the item counts against.
    /// </summary>
    public bool IsReel =>
        string.Equals(MediaProductType, "REELS", StringComparison.OrdinalIgnoreCase);

    /// <summary>photo / carousel / video — the vocabulary the instance config filters on.</summary>
    public string MediaTypeKey => MediaType?.ToUpperInvariant() switch
    {
        "CAROUSEL_ALBUM" => "carousel",
        "VIDEO" => "video",
        "IMAGE" => "photo",
        _ => "photo",
    };
}

public sealed record MetaFeedChild(string ExternalId, string? MediaType, string? MediaUrl, string? ThumbnailUrl);

/// <summary>A page of feed items plus the cursor to continue from, if there is more.</summary>
public sealed record MetaFeedPage(IReadOnlyList<MetaFeedItem> Items, string? NextCursor);

/// <summary>
/// Reads content from the Meta Graph API. The OAuth half lives in <see cref="MetaOAuthClient"/>.
///
/// <para>Nothing here paginates on its own. The caller decides when to stop, because the stop
/// condition is a tenant's configured cap and the whole point of the caps is that a sync never
/// walks an account's history.</para>
/// </summary>
public sealed class MetaGraphClient(
    HttpClient http, IOptions<MetaSocialOptions> options, ILogger<MetaGraphClient> logger)
{
    private readonly MetaSocialOptions _opts = options.Value;

    private string FacebookHost =>
        $"{(_opts.OverrideBaseUrl?.TrimEnd('/') ?? "https://graph.facebook.com")}/{_opts.GraphVersion}";

    private string InstagramHost =>
        _opts.OverrideBaseUrl?.TrimEnd('/') ?? "https://graph.instagram.com";

    private const string InstagramFields =
        "id,caption,media_type,media_product_type,media_url,permalink,thumbnail_url,timestamp,username," +
        "children{id,media_type,media_url,thumbnail_url}";

    private const string FacebookFields =
        "id,message,created_time,permalink_url,full_picture,attachments{media_type,media{image{src}},subattachments}";

    /// <summary>The most recent rate-limit reading Meta reported. Null until a call is made.</summary>
    public MetaUsage? LastUsage { get; private set; }

    /// <summary>
    /// One page of an Instagram account's media. Feed posts and reels come back mixed;
    /// the caller separates them by <see cref="MetaFeedItem.IsReel"/>.
    /// </summary>
    public async Task<MetaFeedPage> GetInstagramMediaAsync(
        string igUserId, string accessToken, bool viaFacebook, string? after, int limit, CancellationToken ct)
    {
        var host = viaFacebook ? FacebookHost : InstagramHost;
        var url = $"{host}/{igUserId}/media?fields={InstagramFields}&limit={limit}" +
                  $"&access_token={Uri.EscapeDataString(accessToken)}" +
                  (after is { Length: > 0 } ? $"&after={Uri.EscapeDataString(after)}" : string.Empty);

        var envelope = await GetAsync<InstagramMediaEnvelope>(url, ct);
        return new MetaFeedPage(
            (envelope.Data ?? []).Select(ToFeedItem).ToList(),
            envelope.Paging?.Cursors?.After);
    }

    /// <summary>
    /// The account's live stories. Only reachable on the Facebook-linked path, and only ever
    /// a handful of items — Meta expires them after 24 hours, so there is no paging to do.
    /// </summary>
    public async Task<IReadOnlyList<MetaFeedItem>> GetInstagramStoriesAsync(
        string igUserId, string accessToken, CancellationToken ct)
    {
        var url = $"{FacebookHost}/{igUserId}/stories?fields={InstagramFields}" +
                  $"&access_token={Uri.EscapeDataString(accessToken)}";

        var envelope = await GetAsync<InstagramMediaEnvelope>(url, ct);
        return (envelope.Data ?? []).Select(ToFeedItem).ToList();
    }

    /// <summary>One page of a Facebook Page's published posts. Requires the Page token.</summary>
    public async Task<MetaFeedPage> GetFacebookPostsAsync(
        string pageId, string pageToken, string? after, int limit, CancellationToken ct)
    {
        var url = $"{FacebookHost}/{pageId}/published_posts?fields={FacebookFields}&limit={limit}" +
                  $"&access_token={Uri.EscapeDataString(pageToken)}" +
                  (after is { Length: > 0 } ? $"&after={Uri.EscapeDataString(after)}" : string.Empty);

        var envelope = await GetAsync<FacebookPostsEnvelope>(url, ct);
        return new MetaFeedPage(
            (envelope.Data ?? []).Select(ToFeedItem).ToList(),
            envelope.Paging?.Cursors?.After);
    }

    private static MetaFeedItem ToFeedItem(InstagramMediaNode n) => new(
        n.Id, n.Caption, n.Permalink, n.MediaType, n.MediaProductType, n.MediaUrl, n.ThumbnailUrl,
        n.Timestamp, n.Username,
        (n.Children?.Data ?? []).Select(c => new MetaFeedChild(c.Id, c.MediaType, c.MediaUrl, c.ThumbnailUrl)).ToList());

    private static MetaFeedItem ToFeedItem(FacebookPostNode n)
    {
        // Facebook has no media_type on a post. full_picture is present for anything with an
        // image; the attachment's media_type tells photo from video from link.
        var attachment = n.Attachments?.Data?.FirstOrDefault();
        var mediaType = attachment?.MediaType?.ToUpperInvariant() switch
        {
            "PHOTO" => "IMAGE",
            "VIDEO" or "VIDEO_INLINE" => "VIDEO",
            "ALBUM" => "CAROUSEL_ALBUM",
            _ => n.FullPicture is { Length: > 0 } ? "IMAGE" : null,
        };

        return new MetaFeedItem(
            n.Id, n.Message, n.PermalinkUrl, mediaType, MediaProductType: "FEED",
            n.FullPicture, ThumbnailUrl: null, n.CreatedTime, Username: null, Children: []);
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var res = await http.GetAsync(url, ct);

        LastUsage = MetaUsage.FromHeaders(res);
        if (LastUsage is { ShouldBackOff: true })
        {
            // Worth a warning even on a success: by the time Meta starts rejecting calls the
            // feed is already broken, and the usage headers are the only advance notice.
            logger.LogWarning(
                "Meta rate-limit usage at {Percent}% of a bucket; the sync should be backing off.",
                LastUsage.WorstPercent);
        }

        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            // The URL carries the access token, so it is never logged.
            logger.LogError("Meta Graph GET -> {Status}: {Body}", (int)res.StatusCode, body);
            throw new MetaApiException(res.StatusCode, body);
        }

        return await res.Content.ReadFromJsonAsync<T>(ct)
               ?? throw new MetaApiException(res.StatusCode, "Empty response body.");
    }

    // ---------- wire DTOs ----------

    private sealed record InstagramMediaEnvelope(
        [property: JsonPropertyName("data")] List<InstagramMediaNode>? Data,
        [property: JsonPropertyName("paging")] Paging? Paging);

    private sealed record InstagramMediaNode(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("caption")] string? Caption,
        [property: JsonPropertyName("media_type")] string? MediaType,
        [property: JsonPropertyName("media_product_type")] string? MediaProductType,
        [property: JsonPropertyName("media_url")] string? MediaUrl,
        [property: JsonPropertyName("permalink")] string? Permalink,
        [property: JsonPropertyName("thumbnail_url")] string? ThumbnailUrl,
        [property: JsonPropertyName("timestamp")] DateTimeOffset? Timestamp,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("children")] ChildrenEnvelope? Children);

    private sealed record ChildrenEnvelope(
        [property: JsonPropertyName("data")] List<InstagramChildNode>? Data);

    private sealed record InstagramChildNode(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("media_type")] string? MediaType,
        [property: JsonPropertyName("media_url")] string? MediaUrl,
        [property: JsonPropertyName("thumbnail_url")] string? ThumbnailUrl);

    private sealed record FacebookPostsEnvelope(
        [property: JsonPropertyName("data")] List<FacebookPostNode>? Data,
        [property: JsonPropertyName("paging")] Paging? Paging);

    private sealed record FacebookPostNode(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("created_time")] DateTimeOffset? CreatedTime,
        [property: JsonPropertyName("permalink_url")] string? PermalinkUrl,
        [property: JsonPropertyName("full_picture")] string? FullPicture,
        [property: JsonPropertyName("attachments")] AttachmentsEnvelope? Attachments);

    private sealed record AttachmentsEnvelope(
        [property: JsonPropertyName("data")] List<AttachmentNode>? Data);

    private sealed record AttachmentNode(
        [property: JsonPropertyName("media_type")] string? MediaType);

    private sealed record Paging(
        [property: JsonPropertyName("cursors")] Cursors? Cursors);

    private sealed record Cursors(
        [property: JsonPropertyName("after")] string? After);
}

/// <summary>
/// Meta's rate-limit reading, taken from the <c>X-App-Usage</c> and
/// <c>X-Business-Use-Case-Usage</c> response headers.
///
/// <para>Meta does not queue: once a bucket hits 100% it rejects calls outright, for up to an
/// hour. Reading these on the way past is the difference between easing off and finding out.</para>
/// </summary>
public sealed record MetaUsage(int WorstPercent)
{
    /// <summary>Back off well before the cliff — the numbers move in jumps under load.</summary>
    public bool ShouldBackOff => WorstPercent >= 80;

    public static MetaUsage? FromHeaders(HttpResponseMessage res)
    {
        var worst = 0;
        var found = false;

        foreach (var header in new[] { "X-App-Usage", "X-Business-Use-Case-Usage" })
        {
            if (!res.Headers.TryGetValues(header, out var values)) continue;
            foreach (var value in values)
            {
                if (TryWorst(value, out var percent))
                {
                    found = true;
                    worst = Math.Max(worst, percent);
                }
            }
        }

        return found ? new MetaUsage(worst) : null;
    }

    /// <summary>
    /// Both headers are JSON, but with different shapes — a flat object of percentages for
    /// X-App-Usage, and an object of arrays keyed by business id for the other. Rather than
    /// model both, walk the tree and take the largest number: every value in either shape is a
    /// percentage, and the largest is the one that matters.
    /// </summary>
    private static bool TryWorst(string json, out int percent)
    {
        percent = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            percent = Walk(doc.RootElement);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }

        static int Walk(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out var n) ? n : 0,
            JsonValueKind.Object => element.EnumerateObject().Select(p => Walk(p.Value)).DefaultIfEmpty(0).Max(),
            JsonValueKind.Array => element.EnumerateArray().Select(Walk).DefaultIfEmpty(0).Max(),
            _ => 0,
        };
    }
}
