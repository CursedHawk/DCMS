using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Caching;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.ContentApi.Social;

/// <summary>
/// Live Instagram stories on the public delivery API.
///
/// <para><b>It answers exactly like a content type on purpose.</b> Same
/// <c>/api/{slug}/{contentType}</c> shape, same <c>{ items, page, pageSize, totalCount }</c>
/// envelope, same per-item fields as a synced post. That is what lets the auto-generated
/// GrapesJS block, <c>hydrate.js</c>'s list layouts and the tenant's OpenAPI document all
/// handle stories with no code of their own — the alternative was a bespoke endpoint plus a
/// bespoke block plus a bespoke renderer, for content that is a list of pictures.</para>
///
/// <para><b>Why it is not a content type.</b> Stories expire after 24 hours. Syncing them
/// would mean a background job racing a clock to mirror media that is guaranteed to be dead
/// before most visitors arrive.</para>
///
/// <para>The literal <c>instagram-story</c> segment outranks <c>DeliveryEndpoints</c>'
/// <c>{contentType}</c> parameter in ASP.NET routing, so this handler wins. The Instagram
/// plugin also declines to declare the type as a route, so if that precedence ever changed the
/// generic handler would 404 rather than serve an empty list from a table nothing writes to.</para>
/// </summary>
public static class StoryDeliveryEndpoints
{
    public const string ContentType = "instagram-story";

    /// <summary>The admin-api client registered in Program.cs.</summary>
    public const string HttpClientName = "admin-api";

    /// <summary>Scope for the service token. Narrower than <c>dcms.admin</c>, deliberately.</summary>
    private const string SocialScope = "dcms.social";

    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    public static IEndpointRouteBuilder MapStoryDelivery(this IEndpointRouteBuilder app)
    {
        app.MapGet($"/api/{{slug}}/{ContentType}", async (
            string slug, int? page, int? pageSize,
            ITenantContext tenant, CmsDbContext db, ICacheService cache,
            IServiceTokenProvider tokens, IHttpClientFactory httpFactory,
            IConfiguration configuration, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId) return Results.NotFound();

            var instance = await db.PluginInstances.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Slug == slug && p.Enabled, ct);

            if (instance is null || instance.PluginId != "instagram") return Results.NotFound();

            var logger = loggers.CreateLogger("Dcms.ContentApi.Social.Stories");
            var key = CacheKey(tenantId, instance.Id);

            var json = await cache.GetAsync<string>(key, ct);
            if (json is null)
            {
                // Cached even when it comes back empty. A tenant with stories switched off, or
                // an account that simply has none today, is the common case — and it is the one
                // that would otherwise call admin-api and Meta on every single page view.
                json = await FetchAsync(tokens, httpFactory, tenantId, instance.Id, logger, ct);
                if (json is null)
                {
                    // Upstream is unhappy. Serve an empty feed rather than failing the request:
                    // a story strip is decoration, and a 500 here would take the page with it.
                    // Deliberately not cached, so the next request retries.
                    return Results.Ok(Empty(page, pageSize));
                }

                await cache.SetAsync(key, json, Ttl(configuration), ct);
            }

            var stories = JsonNode.Parse(json)?.AsArray() ?? [];
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            var current = Math.Max(page ?? 1, 1);

            var items = stories
                .Skip((current - 1) * size).Take(size)
                .Select(node => ToDto(instance.Id, node!.AsObject()))
                .ToList();

            return Results.Ok(new PagedResult<ContentItemDto>(items, current, size, stories.Count));
        });

        return app;
    }

    public static string CacheKey(Guid tenantId, Guid instanceId) =>
        $"t:{tenantId}:social:stories:{instanceId}";

    private static TimeSpan Ttl(IConfiguration configuration) =>
        configuration.GetValue("Social:StoryCacheSeconds", 0) is var seconds && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultTtl;

    private static PagedResult<ContentItemDto> Empty(int? page, int? pageSize) =>
        new([], Math.Max(page ?? 1, 1), Math.Clamp(pageSize ?? 20, 1, 100), 0);

    /// <summary>
    /// Asks admin-api for the account's live stories. Returns the raw items array as a string
    /// — the shape the cache holds, because a <see cref="JsonElement"/> does not survive a
    /// round trip through it (the same reason <c>PublishedContentReader</c> caches strings).
    /// Null means the call failed and nothing should be cached.
    /// </summary>
    private static async Task<string?> FetchAsync(
        IServiceTokenProvider tokens, IHttpClientFactory httpFactory,
        Guid tenantId, Guid instanceId, ILogger logger, CancellationToken ct)
    {
        try
        {
            var token = await tokens.GetTokenAsync(SocialScope, ct);

            var client = httpFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/internal/social/stories")
            {
                Content = JsonContent.Create(new { tenantId, instanceId }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "admin-api returned {Status} for stories on instance {InstanceId}.",
                    (int)response.StatusCode, instanceId);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return payload.TryGetProperty("items", out var items)
                ? items.GetRawText()
                : "[]";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Could not read Instagram stories for instance {InstanceId}.", instanceId);
            return null;
        }
    }

    /// <summary>
    /// Dresses a story as a content item. The field names match
    /// <c>MetaFeedContentTypes</c> exactly, which is what lets one builder block and one
    /// template render posts, reels and stories without knowing which it was handed.
    /// </summary>
    private static ContentItemDto ToDto(Guid instanceId, JsonObject story)
    {
        var externalId = story["externalId"]?.GetValue<string>() ?? string.Empty;

        var data = new JsonObject
        {
            ["externalId"] = externalId,
            ["permalink"] = story["permalink"]?.GetValue<string>(),
            ["caption"] = story["caption"]?.GetValue<string>(),
            // Never a mirrored asset: a story is gone tomorrow, so there is nothing to mirror.
            ["media"] = null,
            ["thumbnail"] = null,
            ["mediaType"] = story["mediaType"]?.GetValue<string>(),
            ["mediaUrl"] = story["mediaUrl"]?.GetValue<string>(),
            ["postedAt"] = story["postedAt"]?.GetValue<DateTimeOffset?>()?.ToString("O"),
            ["username"] = story["username"]?.GetValue<string>(),
            ["children"] = new JsonArray(),
        };

        return new ContentItemDto(
            StableId(instanceId, externalId),
            instanceId,
            ContentType,
            externalId,
            VersionNo: 1,
            JsonDocument.Parse(data.ToJsonString()).RootElement,
            story["postedAt"]?.GetValue<DateTimeOffset?>() ?? DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// A story has no database row, but the envelope promises an id and front-end list code
    /// keys on it. Deriving it from the instance and the Meta media id keeps it stable across
    /// requests and cache refreshes — a fresh <c>Guid.NewGuid()</c> per call would re-key every
    /// item on every poll and make any client-side list thrash.
    /// </summary>
    private static Guid StableId(Guid instanceId, string externalId) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"{instanceId}:{externalId}")));
}
