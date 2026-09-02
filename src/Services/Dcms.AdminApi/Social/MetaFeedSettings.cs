using System.Text.Json;
using Dcms.Plugins.Meta.Core;

namespace Dcms.AdminApi.Social;

/// <summary>
/// One plugin instance's sync settings, read out of its raw config JSON.
///
/// <para>Every cap is clamped on the way in. The admin form and
/// <c>PluginConfigValidator</c> both enforce the ceiling already, but this is the code that
/// actually spends the budget, and a value that arrived some other way — an older row written
/// before the schema tightened, a direct database edit — must not be able to turn a feed into
/// a scraper.</para>
/// </summary>
public sealed record MetaFeedSettings(
    Guid ConnectionId,
    int MaxPosts,
    int MaxReels,
    IReadOnlyCollection<string> MediaTypes,
    DateTimeOffset? SyncSince,
    bool MirrorVideo,
    bool ShowStories,
    int SyncIntervalMinutes)
{
    public static MetaFeedSettings? Read(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(configJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // No connection means the instance is installed but not connected yet. That is a
            // normal state, not an error — there is simply nothing to sync.
            if (!root.TryGetProperty(MetaFeedConfig.ConnectionIdKey, out var connection)
                || connection.ValueKind != JsonValueKind.String
                || !Guid.TryParse(connection.GetString(), out var connectionId))
            {
                return null;
            }

            return new MetaFeedSettings(
                connectionId,
                Cap(root, MetaFeedConfig.MaxPostsKey, MetaFeedConfig.DefaultMaxPosts),
                Cap(root, MetaFeedConfig.MaxReelsKey, MetaFeedConfig.DefaultMaxReels),
                ReadMediaTypes(root),
                ReadDate(root, MetaFeedConfig.SyncSinceKey),
                ReadBool(root, MetaFeedConfig.MirrorVideoKey),
                ReadBool(root, MetaFeedConfig.ShowStoriesKey),
                Math.Clamp(ReadInt(root, MetaFeedConfig.SyncIntervalKey, MetaFeedConfig.DefaultSyncIntervalMinutes), 5, 1440));
        }
    }

    private static int Cap(JsonElement root, string key, int fallback) =>
        Math.Clamp(ReadInt(root, key, fallback), 0, MetaFeedConfig.MaxItemsCeiling);

    private static int ReadInt(JsonElement root, string key, int fallback) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : fallback;

    private static bool ReadBool(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? ReadDate(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Absent means "all of them". An empty array, by contrast, means the admin deliberately
    /// deselected everything — and must sync nothing rather than silently everything.
    /// </summary>
    private static IReadOnlyCollection<string> ReadMediaTypes(JsonElement root)
    {
        if (!root.TryGetProperty(MetaFeedConfig.MediaTypesKey, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return AllMediaTypes;
        }

        return v.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> AllMediaTypes =
        new(["photo", "carousel", "video", "link", "text"], StringComparer.OrdinalIgnoreCase);

    public bool Accepts(MetaFeedItem item) =>
        MediaTypes.Contains(item.MediaTypeKey)
        && (SyncSince is not { } since || item.PostedAt is null || item.PostedAt >= since);

    /// <summary>The cap this item counts against — reels and feed posts are budgeted apart.</summary>
    public int CapFor(MetaFeedItem item) => item.IsReel ? MaxReels : MaxPosts;
}
