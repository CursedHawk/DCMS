using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// A stand-in for the YouTube Data API v3, served in-process on a dynamic port and handed to
/// admin-api as <c>YouTube:OverrideBaseUrl</c>. It keeps a request log because the sync's page
/// ceiling is a claim about quota, and only a counted stub can hold it honest.
/// </summary>
public sealed class YouTubeStubServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private YouTubeStubServer(WebApplication app) => _app = app;

    public string BaseUrl { get; private set; } = string.Empty;

    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Channels by handle (without @). Value: (channel id, title).</summary>
    public ConcurrentDictionary<string, (string Id, string Title)> Handles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Playlist id → its video ids in order. Absent = 404, as YouTube answers.</summary>
    public ConcurrentDictionary<string, List<string>> Playlists { get; } = new();

    /// <summary>Playlist id → title, for <c>playlists.list</c>.</summary>
    public ConcurrentDictionary<string, string> PlaylistTitles { get; } = new();

    public ConcurrentDictionary<string, Video> Videos { get; } = new();

    public sealed record Video(string Id, DateTimeOffset PublishedAt, long Views = 10, bool Live = false);

    public int RequestsFor(string fragment) => Requests.Count(r => r.Contains(fragment, StringComparison.Ordinal));

    public static async Task<YouTubeStubServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        var stub = new YouTubeStubServer(app);

        app.Use(async (http, next) =>
        {
            if (http.Request.Query["key"] != "test-youtube-key")
            {
                http.Response.StatusCode = 400;
                return;
            }
            stub.Requests.Enqueue(http.Request.Path + http.Request.QueryString);
            await next();
        });

        app.MapGet("/youtube/v3/channels", (HttpContext http) =>
        {
            var handle = http.Request.Query["forHandle"].ToString().TrimStart('@');
            var id = http.Request.Query["id"].ToString();
            var match = stub.Handles.Values.FirstOrDefault(c => c.Id == id);
            if (handle.Length > 0 && stub.Handles.TryGetValue(handle, out var byHandle)) match = byHandle;
            return Results.Json(new
            {
                items = match.Id is null
                    ? []
                    : new[]
                    {
                        new
                        {
                            id = match.Id,
                            snippet = new
                            {
                                title = match.Title, customUrl = "@" + handle,
                                thumbnails = new { @default = new { url = "https://yt3.test/avatar.jpg" } },
                            },
                            contentDetails = new { relatedPlaylists = new { uploads = "UU" + match.Id[2..] } },
                        },
                    },
            });
        });

        app.MapGet("/youtube/v3/playlists", (HttpContext http) =>
        {
            var id = http.Request.Query["id"].ToString();
            return Results.Json(new
            {
                items = stub.PlaylistTitles.TryGetValue(id, out var title)
                    ? new[] { new { id, snippet = new { title, channelId = (string?)null, channelTitle = "Acme" } } }
                    : [],
            });
        });

        app.MapGet("/youtube/v3/playlistItems", (HttpContext http) =>
        {
            var id = http.Request.Query["playlistId"].ToString();
            if (!stub.Playlists.TryGetValue(id, out var all))
            {
                return Results.Json(
                    new { error = new { code = 404, message = "Not found", errors = new[] { new { reason = "playlistNotFound" } } } },
                    statusCode: 404);
            }
            var size = int.Parse(http.Request.Query["maxResults"].ToString());
            var offset = int.TryParse(http.Request.Query["pageToken"], out var o) ? o : 0;
            var page = all.Skip(offset).Take(size).ToList();
            return Results.Json(new
            {
                items = page.Select(v => new { contentDetails = new { videoId = v } }),
                nextPageToken = offset + size < all.Count ? (offset + size).ToString() : null,
                pageInfo = new { totalResults = all.Count },
            });
        });

        app.MapGet("/youtube/v3/videos", (HttpContext http) =>
        {
            var ids = http.Request.Query["id"].ToString().Split(',');
            return Results.Json(new
            {
                items = ids.Where(stub.Videos.ContainsKey).Select(id => stub.Videos[id]).Select(v => new
                {
                    id = v.Id,
                    snippet = new
                    {
                        title = $"Video {v.Id}",
                        description = $"About {v.Id}",
                        publishedAt = v.PublishedAt.ToString("O"),
                        channelTitle = "Acme",
                        thumbnails = new { high = new { url = $"https://i.ytimg.test/vi/{v.Id}/hq.jpg" } },
                        liveBroadcastContent = "none",
                    },
                    contentDetails = new { duration = "PT1M5S" },
                    statistics = new { viewCount = v.Views.ToString(), likeCount = "3" },
                    liveStreamingDetails = v.Live ? new { actualStartTime = v.PublishedAt.ToString("O") } : null,
                }),
            });
        });

        await app.StartAsync();
        stub.BaseUrl = app.Urls.First();
        return stub;
    }

    public void Reset()
    {
        Requests.Clear();
        Handles.Clear();
        Playlists.Clear();
        PlaylistTitles.Clear();
        Videos.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
