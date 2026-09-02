using Dcms.Plugins.Facebook;
using Dcms.Plugins.Instagram;
using Dcms.Shared.Data.Social;

namespace Dcms.AdminApi.Social;

/// <summary>
/// Pulls exactly as much of an account as the instance's caps allow, and not one page more.
///
/// <para>This is the piece the whole "how much do we sync" requirement rests on, so it is
/// separated from the worker that stores the results — it has no database and no clock, which
/// makes the one property that matters directly testable: given an account of 500 posts and a
/// cap of 5, how many requests does it make?</para>
///
/// <para>The naive shape — fetch everything, then take the first N — passes every test that
/// only counts stored rows, and is catastrophic in production: it burns the rate-limit budget
/// of every tenant on a history nobody asked for.</para>
/// </summary>
public sealed class MetaFeedFetcher(MetaGraphClient graph, ILogger<MetaFeedFetcher> logger)
{
    /// <summary>
    /// Meta caps a page at 100 for these edges. Asking for more than the remaining budget
    /// wastes nothing, but asking for a whole page when two items are wanted does.
    /// </summary>
    private const int MaxPageSize = 100;

    /// <summary>A safety stop, independent of the caps, so a paging bug cannot loop forever.</summary>
    private const int MaxPages = 20;

    public sealed record Harvest(IReadOnlyList<MetaFeedItem> Items, int PagesFetched);

    public async Task<Harvest> FetchAsync(
        MetaConnection connection,
        string accessToken,
        string pluginId,
        MetaFeedSettings settings,
        CancellationToken ct)
    {
        var wanted = new Dictionary<string, int>(StringComparer.Ordinal);
        if (pluginId == InstagramPlugin.PluginId)
        {
            wanted[InstagramPlugin.PostType] = settings.MaxPosts;
            wanted[InstagramPlugin.ReelType] = settings.MaxReels;
        }
        else
        {
            wanted[FacebookPlugin.PostType] = settings.MaxPosts;
        }

        var collected = new List<MetaFeedItem>();
        var counts = wanted.Keys.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
        string? cursor = null;
        var pages = 0;

        // Nothing wanted at all (every cap set to zero) must cost zero requests, not one.
        if (wanted.Values.All(v => v == 0)) return new Harvest([], 0);

        while (pages < MaxPages)
        {
            // Ask for what is still missing, not for a full page. With a cap of 5 on a fresh
            // account this is a single request for 5 items.
            var remaining = wanted.Sum(w => Math.Max(0, w.Value - counts[w.Key]));
            var limit = Math.Clamp(remaining, 1, MaxPageSize);

            var page = connection.Provider == MetaProvider.Facebook && pluginId == FacebookPlugin.PluginId
                ? await graph.GetFacebookPostsAsync(connection.ExternalAccountId, accessToken, cursor, limit, ct)
                : await graph.GetInstagramMediaAsync(
                    connection.ExternalAccountId, accessToken,
                    viaFacebook: connection.Provider == MetaProvider.Facebook, cursor, limit, ct);

            pages++;

            foreach (var item in page.Items)
            {
                if (!settings.Accepts(item)) continue;

                var type = TypeOf(item, pluginId);
                if (!counts.TryGetValue(type, out var have) || have >= wanted[type]) continue;

                counts[type] = have + 1;
                collected.Add(item);
            }

            // Every bucket full, or Meta has no more to give.
            if (wanted.All(w => counts[w.Key] >= w.Value)) break;
            if (page.NextCursor is not { Length: > 0 }) break;

            // Meta rejects outright once a bucket reaches 100%, for up to an hour. Stopping a
            // page early costs this tenant a slightly stale feed; carrying on costs every
            // tenant on the app the next hour of syncs.
            if (graph.LastUsage is { ShouldBackOff: true })
            {
                logger.LogWarning(
                    "Stopping the sync for {Account} early at {Percent}% rate-limit usage.",
                    connection.ExternalAccountId, graph.LastUsage.WorstPercent);
                break;
            }

            cursor = page.NextCursor;
        }

        return new Harvest(collected, pages);
    }

    /// <summary>Which content type an item belongs to, given the plugin that asked for it.</summary>
    public static string TypeOf(MetaFeedItem item, string pluginId) =>
        pluginId == InstagramPlugin.PluginId
            ? item.IsReel ? InstagramPlugin.ReelType : InstagramPlugin.PostType
            : FacebookPlugin.PostType;
}
