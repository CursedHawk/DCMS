using System.Text.Json;

namespace Dcms.Plugins.Meta.Core;

/// <summary>
/// The two feed plugins' identities, here rather than on the plugin classes because the
/// shared Meta machinery (sync, OAuth, stories) serves both and the plugins reference it —
/// not the other way round.
/// </summary>
public static class MetaPlugins
{
    public const string InstagramId = "instagram";
    public const string InstagramPost = "instagram-post";
    public const string InstagramReel = "instagram-reel";
    public const string InstagramStory = "instagram-story";

    public const string FacebookId = "facebook";
    public const string FacebookPost = "facebook-post";

    public static readonly string[] FeedPluginIds = [InstagramId, FacebookId];

    /// <summary>
    /// Reads the stories toggle out of instance config, tolerating an absent or malformed
    /// value. Absence means off: an instance that has never been configured must not start
    /// advertising a live Meta call in its public API document.
    /// </summary>
    public static bool StoriesEnabled(JsonDocument config) =>
        config.RootElement.ValueKind == JsonValueKind.Object
        && config.RootElement.TryGetProperty(MetaFeedConfig.ShowStoriesKey, out var value)
        && value.ValueKind == JsonValueKind.True;
}
