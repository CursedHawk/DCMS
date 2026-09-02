namespace Dcms.Plugins.Meta.Core;

/// <summary>
/// The instance-config contract shared by both feed plugins: which account to pull from,
/// how often, and — the part that matters most — how much.
///
/// <para>An account can hold thousands of posts. Nothing here is optional or unbounded:
/// every cap has a default, and a maximum the server re-checks, because the caps are what
/// stand between one enabled plugin and a background job that walks an entire Instagram
/// history through an image pipeline on every poll.</para>
/// </summary>
public static class MetaFeedConfig
{
    /// <summary>
    /// The hard ceiling on any per-type cap.
    ///
    /// <para>It is in the JSON Schema so the admin form enforces it, and re-checked server-side
    /// because a schema is only the client's story: the config endpoint takes JSON, and
    /// <c>PluginConfigValidator</c> is the thing that actually decides.</para>
    /// </summary>
    public const int MaxItemsCeiling = 200;

    public const int DefaultMaxPosts = 24;
    public const int DefaultMaxReels = 12;
    public const int DefaultSyncIntervalMinutes = 15;

    public const string ConnectionIdKey = "connectionId";
    public const string MaxPostsKey = "maxPosts";
    public const string MaxReelsKey = "maxReels";
    public const string MediaTypesKey = "mediaTypes";
    public const string SyncSinceKey = "syncSince";
    public const string MirrorVideoKey = "mirrorVideo";
    public const string ShowStoriesKey = "showStories";
    public const string SyncIntervalKey = "syncIntervalMinutes";
    public const string AccountUsernameKey = "accountUsername";

    /// <summary>
    /// Config keys a public site may read through <c>GET /api/{slug}/_config</c>.
    ///
    /// <para>Short on purpose. <c>connectionId</c> is absent: it is not a secret in itself, but
    /// it is the handle to one, and the allow-list exists precisely so that adding a field near
    /// a credential cannot quietly publish it.</para>
    ///
    /// <para>Derived from the same flags that build the schema rather than written out as a
    /// constant, because the two must agree: a key exposed here that the schema does not
    /// declare is a promise the plugin cannot keep, and Facebook briefly published
    /// <c>showStories</c> that way — a key it has no concept of.</para>
    /// </summary>
    public static string[] PublicKeys(bool includeStories) =>
        includeStories ? [AccountUsernameKey, ShowStoriesKey] : [AccountUsernameKey];

    /// <summary>
    /// Builds the instance config schema. <paramref name="mediaTypes"/> differs per provider,
    /// and <paramref name="includeReels"/> and <paramref name="includeStories"/> are Instagram's.
    /// </summary>
    public static string Schema(
        IReadOnlyList<string> mediaTypes,
        bool includeReels,
        bool includeStories)
    {
        var reels = includeReels
            ? $$"""
                ,
                    "{{MaxReelsKey}}": {
                      "type": "integer", "title": "Reels to keep",
                      "description": "How many reels to sync. 0 turns reels off entirely.",
                      "minimum": 0, "maximum": {{MaxItemsCeiling}}, "default": {{DefaultMaxReels}}
                    }
                """
            : string.Empty;

        var stories = includeStories
            ? $$"""
                ,
                    "{{ShowStoriesKey}}": {
                      "type": "boolean", "title": "Serve stories",
                      "description": "Stories are fetched live rather than synced, and are only available for an Instagram account linked to a Facebook Page.",
                      "default": false
                    }
                """
            : string.Empty;

        var typeEnum = string.Join(", ", mediaTypes.Select(t => $"\"{t}\""));

        return $$"""
            {
              "type": "object",
              "properties": {
                "{{ConnectionIdKey}}": {
                  "type": "string", "format": "meta-connection", "title": "Connected account",
                  "description": "Which connected Meta account this feed pulls from."
                },
                "{{AccountUsernameKey}}": {
                  "type": "string", "title": "Display handle",
                  "description": "Shown alongside the feed on the site. Readable publicly."
                },
                "{{MaxPostsKey}}": {
                  "type": "integer", "title": "Posts to keep",
                  "description": "How many posts to sync. The sync stops requesting pages once this many are found, so it never walks the whole account.",
                  "minimum": 0, "maximum": {{MaxItemsCeiling}}, "default": {{DefaultMaxPosts}}
                }{{reels}},
                "{{MediaTypesKey}}": {
                  "type": "array", "title": "Media types",
                  "description": "Which kinds of post to sync at all.",
                  "items": { "type": "string", "enum": [{{typeEnum}}] },
                  "default": [{{typeEnum}}], "uniqueItems": true
                },
                "{{SyncSinceKey}}": {
                  "type": "string", "format": "date", "title": "Only sync posts after",
                  "description": "Optional. Nothing older than this date is ever pulled in."
                },
                "{{MirrorVideoKey}}": {
                  "type": "boolean", "title": "Mirror video into DCMS media",
                  "description": "Off by default: video mirroring runs transcoding, which is expensive. With it off, video is linked from Meta's CDN instead.",
                  "default": false
                },
                "{{SyncIntervalKey}}": {
                  "type": "integer", "title": "Sync every (minutes)",
                  "minimum": 5, "maximum": 1440, "default": {{DefaultSyncIntervalMinutes}}
                }{{stories}}
              },
              "required": ["{{ConnectionIdKey}}"],
              "additionalProperties": false
            }
            """;
    }
}
