using System.Globalization;
using System.Text.Json;

namespace Dcms.Plugins.YouTube;

/// <summary>
/// An instance's config: where the videos come from, and how many to keep.
///
/// <para>Every cap has a ceiling the server enforces (<c>PluginConfigValidator</c> checks the
/// schema on save) and <see cref="Read"/> clamps again, because the caps are what bound the
/// quota one instance can spend.</para>
/// </summary>
public sealed record YouTubeSettings(
    string? Channel,
    string? Playlist,
    int MaxVideos,
    bool IncludeShorts,
    bool IncludeLive,
    DateOnly? SyncSince)
{
    public const int MaxVideosCeiling = 200;
    public const int DefaultMaxVideos = 12;

    public const string ChannelKey = "channel";
    public const string PlaylistKey = "playlist";
    public const string MaxVideosKey = "maxVideos";
    public const string IncludeShortsKey = "includeShorts";
    public const string IncludeLiveKey = "includeLive";
    public const string SyncSinceKey = "syncSince";
    public const string ChannelTitleKey = "channelTitle";

    public static readonly string Schema = $$"""
        {
          "type": "object",
          "properties": {
            "{{ChannelKey}}": {
              "type": "string", "format": "youtube-channel", "title": "Channel", "maxLength": 300,
              "description": "The channel's address, its @handle or its id — copy it from the browser."
            },
            "{{PlaylistKey}}": {
              "type": "string", "title": "Playlist", "maxLength": 300,
              "description": "Optional. A playlist's address: its videos are shown instead of the channel's latest uploads."
            },
            "{{MaxVideosKey}}": {
              "type": "integer", "title": "Videos to keep",
              "description": "How many of the latest videos to sync. The sync stops reading once it has this many, so it never walks the whole channel.",
              "minimum": 1, "maximum": {{MaxVideosCeiling}}, "default": {{DefaultMaxVideos}}
            },
            "{{IncludeShortsKey}}": {
              "type": "boolean", "title": "Include Shorts", "default": false
            },
            "{{IncludeLiveKey}}": {
              "type": "boolean", "title": "Include live streams and premieres", "default": false
            },
            "{{SyncSinceKey}}": {
              "type": "string", "format": "date", "title": "Only videos after",
              "description": "Optional. Nothing published before this date is synced."
            },
            "{{ChannelTitleKey}}": {
              "type": "string", "title": "Display name", "maxLength": 200,
              "description": "Shown alongside the videos on the site. Readable publicly."
            }
          },
          "additionalProperties": false
        }
        """;

    /// <summary>Null when the instance names neither a channel nor a playlist: nothing to sync yet.</summary>
    public static YouTubeSettings? Read(JsonDocument config)
    {
        var root = config.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var channel = String(root, ChannelKey);
        var playlist = String(root, PlaylistKey);
        if (channel is null && playlist is null) return null;

        var max = root.TryGetProperty(MaxVideosKey, out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var n) ? n : DefaultMaxVideos;
        DateOnly? since = DateOnly.TryParseExact(
            String(root, SyncSinceKey), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

        return new YouTubeSettings(
            channel,
            playlist,
            Math.Clamp(max, 1, MaxVideosCeiling),
            Bool(root, IncludeShortsKey),
            Bool(root, IncludeLiveKey),
            since);
    }

    private static string? String(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static bool Bool(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
}
