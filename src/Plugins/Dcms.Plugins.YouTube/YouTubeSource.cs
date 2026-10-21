using System.Text.RegularExpressions;

namespace Dcms.Plugins.YouTube;

public enum ChannelRefKind
{
    /// <summary>A <c>UC…</c> channel id: needs no lookup to find its uploads.</summary>
    Id,

    /// <summary>An <c>@handle</c>, stored without the <c>@</c>.</summary>
    Handle,

    /// <summary>A legacy <c>/user/name</c> link.</summary>
    Username,
}

public sealed record ChannelRef(ChannelRefKind Kind, string Value);

/// <summary>
/// Turns what an admin pastes — a channel or playlist link copied from the browser, a bare
/// <c>@handle</c>, a channel or playlist id — into something the Data API can be asked about.
/// Pure on purpose: everything a person can paste is a unit test, not a quota-spending call.
/// </summary>
public static partial class YouTubeSource
{
    [GeneratedRegex("^UC[A-Za-z0-9_-]{22}$")]
    private static partial Regex ChannelId();

    [GeneratedRegex(@"^@?[A-Za-z0-9._-]{3,100}$")]
    private static partial Regex Handle();

    [GeneratedRegex("^[A-Za-z0-9_-]{10,64}$")]
    private static partial Regex PlaylistId();

    /// <summary>The channel a link, handle or id names; null when it names none.</summary>
    public static ChannelRef? ParseChannel(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        if (ChannelId().IsMatch(text)) return new ChannelRef(ChannelRefKind.Id, text);

        if (AsYouTubeUri(text) is { } uri)
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return null;
            var first = Uri.UnescapeDataString(segments[0]);

            if (first.StartsWith('@') && Handle().IsMatch(first)) return new ChannelRef(ChannelRefKind.Handle, first[1..]);
            if (segments.Length < 2) return null;
            var second = Uri.UnescapeDataString(segments[1]);

            return first switch
            {
                "channel" when ChannelId().IsMatch(second) => new ChannelRef(ChannelRefKind.Id, second),
                "user" when Handle().IsMatch(second) => new ChannelRef(ChannelRefKind.Username, second),
                // Custom /c/ URLs have no API lookup of their own; most match the channel's handle.
                "c" when Handle().IsMatch(second) => new ChannelRef(ChannelRefKind.Handle, second),
                _ => null,
            };
        }

        // A bare handle. The @ is what makes it unambiguous; without one, a word is accepted
        // only if it cannot be a URL or an id, which the checks above have already ruled out.
        return Handle().IsMatch(text) && !text.Contains('/')
            ? new ChannelRef(ChannelRefKind.Handle, text.TrimStart('@'))
            : null;
    }

    /// <summary>The playlist id in a playlist (or watch-in-playlist) link, or a bare id; null otherwise.</summary>
    public static string? ParsePlaylist(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        if (AsYouTubeUri(text) is { } uri)
        {
            var list = System.Web.HttpUtility.ParseQueryString(uri.Query)["list"];
            return list is not null && PlaylistId().IsMatch(list) ? list : null;
        }

        return PlaylistId().IsMatch(text) && !ChannelId().IsMatch(text) ? text : null;
    }

    /// <summary>A channel's uploads playlist: its id with <c>UC</c> swapped for <c>UU</c>.</summary>
    public static string UploadsPlaylist(string channelId) => "UU" + channelId[2..];

    /// <summary>
    /// A channel's Shorts, as a playlist: <c>UUSH</c> plus the id after <c>UC</c>.
    /// ponytail: undocumented by Google, and the only way the Data API tells a Short apart. If
    /// it ever stops answering, the sync treats nothing as a Short (and logs it); the upgrade
    /// is a HEAD request to youtube.com/shorts/{id} per new video.
    /// </summary>
    public static string ShortsPlaylist(string channelId) => "UUSH" + channelId[2..];

    private static Uri? AsYouTubeUri(string text)
    {
        var candidate = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        return host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com"
            ? uri
            : null;
    }
}
