using System.Text;
using Dcms.AdminApi.Tenancy;
using Dcms.Plugins.Instagram;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Social;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Social;

/// <summary>
/// The one Meta call that is not synced: an account's live stories, read on demand.
///
/// <para><b>Why it lives here rather than in content-api.</b> Stories expire after 24 hours,
/// so mirroring them into content items would be pure waste — they have to be fetched at
/// request time, which means holding the tenant's access token at request time. content-api is
/// the internet-facing service and is granted neither encrypt nor decrypt on
/// <c>dcms-social-tokens</c>; that asymmetry is the whole design, and adding a decrypt grant to
/// the public service to save one hop would undo it. So content-api asks admin-api, and the
/// credential never leaves the admin plane.</para>
///
/// <para><b>Why it is not "internal by network".</b> Caddy routes <c>/api/*</c> on the admin
/// host to admin-api, so a public request for <c>/api/internal/*</c> genuinely arrives. The
/// path is a naming convention, not a boundary — the guard is the client-credentials token and
/// the <c>dcms.social</c> scope, checked by <see cref="ServicePrincipalGuard"/>.</para>
/// </summary>
public static class MetaStoriesEndpoints
{
    public const string Route = "/api/internal/social/stories";

    /// <summary>The scope content-api must hold. Its own, not <c>dcms.admin</c>.</summary>
    public const string Scope = "dcms.social";

    /// <summary>One live story, in the same field vocabulary as a synced post.</summary>
    public sealed record StoryDto(
        string ExternalId,
        string? Permalink,
        string? Caption,
        string? MediaType,
        string? MediaUrl,
        string? ThumbnailUrl,
        DateTimeOffset? PostedAt,
        string? Username);

    public sealed record StoriesRequest(Guid TenantId, Guid InstanceId);

    public static IEndpointRouteBuilder MapMetaStoriesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(Route, async (
            StoriesRequest body, CmsDbContext cms, SocialDbContext social,
            MetaGraphClient graph, ITransitEncryptor encryptor, IAuditRecorder audit,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            var logger = loggers.CreateLogger("Dcms.AdminApi.Social.Stories");

            // No ambient tenant: the caller is a service, not a person in a workspace. Both
            // ids are matched together so a tenant id from the caller can never reach another
            // tenant's instance.
            var instance = await cms.PluginInstances.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(
                    p => p.Id == body.InstanceId && p.TenantId == body.TenantId && p.Enabled, ct);

            if (instance is null || instance.PluginId != InstagramPlugin.PluginId)
            {
                return Results.NotFound();
            }

            var settings = MetaFeedSettings.Read(instance.ConfigJson);

            // Not connected, or stories switched off for this instance. An empty list rather
            // than an error: the site is asking a reasonable question and the answer is "none".
            if (settings is null || !settings.ShowStories) return Ok([]);

            var connection = await social.Connections.IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    c => c.Id == settings.ConnectionId && c.TenantId == body.TenantId, ct);

            if (connection is null || connection.Status != MetaConnectionStatus.Active) return Ok([]);

            // The Instagram-Login path has no /stories edge at all. Returning empty here rather
            // than letting Meta 400 keeps a platform limitation from looking like an outage.
            if (!MetaScopes.SupportsStories(connection.Provider)) return Ok([]);

            audit.Record(AuditActions.SecretAccessed)
                .InTenant(body.TenantId)
                .For("meta_connection", connection.Id, connection.AccountName)
                .With("plugin", instance.PluginId)
                .With("scope", "stories");

            string token;
            try
            {
                var ciphertext = connection.PageTokenCiphertext is { Length: > 0 } page
                    ? page
                    : connection.AccessTokenCiphertext;

                token = Encoding.UTF8.GetString(await encryptor.DecryptAsync(
                    VaultTransitServiceCollectionExtensions.SocialTokensKey, ciphertext, ct));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not decrypt the Meta token for connection {ConnectionId}.", connection.Id);
                return Results.Problem(
                    title: "The stored credential could not be read.",
                    statusCode: StatusCodes.Status502BadGateway);
            }

            try
            {
                var stories = await graph.GetInstagramStoriesAsync(connection.ExternalAccountId, token, ct);

                return Ok(stories.Select(s => new StoryDto(
                    s.ExternalId, s.Permalink, s.Caption, s.MediaType,
                    // Deliberately Meta's own CDN URL. A story is gone in a day, so mirroring
                    // it would run the image pipeline for bytes nobody can request tomorrow.
                    s.MediaUrl, s.ThumbnailUrl, s.PostedAt,
                    s.Username ?? connection.AccountUsername)).ToList());
            }
            catch (MetaApiException ex)
            {
                if (ex.IsTokenInvalid)
                {
                    connection.Status = MetaConnectionStatus.NeedsReauth;
                    connection.LastError = "Meta rejected the stored credential. Reconnect the account.";
                    await social.SaveChangesAsync(ct);
                }

                // 502, not 500: admin-api is fine — the upstream is not. content-api turns this
                // into an empty list so one unhappy Meta call does not break a whole page.
                logger.LogWarning(ex, "Meta stories fetch failed for connection {ConnectionId}.", connection.Id);
                return Results.Problem(
                    title: "Meta could not be reached.", statusCode: StatusCodes.Status502BadGateway);
            }
        }).RequireAuthorization()
          .AuditExempt(
              "A read behind a POST — the body carries ids, it changes nothing, and it runs once "
              + "per cache miss on a public page, so an endpoint record would be high-volume and "
              + "low-signal. The part that is worth recording, the token decrypt, is audited "
              + "inside the handler as SecretAccessed, which is where the actual sensitivity is.")
          .AllowServicePrincipal(Scope,
              "content-api serves live stories on the public delivery API and cannot hold a "
              + "tenant's Meta token; this is the read that keeps the credential on the admin plane.");

        return app;
    }

    private static IResult Ok(IReadOnlyList<StoryDto> items) => Results.Ok(new { items });
}
