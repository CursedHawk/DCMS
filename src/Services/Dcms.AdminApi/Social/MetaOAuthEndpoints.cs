using System.Security.Cryptography;
using System.Text;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Social;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dcms.AdminApi.Social;

/// <summary>
/// Connecting a tenant's Meta account: start consent, take the callback, list and revoke.
///
/// <para>The callback is the unusual one. Meta redirects a browser to it with no bearer token
/// and no tenant header, so it must be anonymous — which means the single-use state row is the
/// entire CSRF defence and the only thing that says which tenant the code belongs to. Every
/// decision below follows from that: the state token is random and stored only as a hash, the
/// row is consumed atomically, and it expires in minutes.</para>
/// </summary>
public static class MetaOAuthEndpoints
{
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Named limiter for the anonymous callback; registered in Program.cs.</summary>
    public const string CallbackRateLimitPolicy = "meta-oauth-callback";

    public static IEndpointRouteBuilder MapMetaOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // ---------- start consent ----------

        app.MapGet("/api/admin/social/{provider}/connect", async (
            string provider, Guid? instanceId, SocialDbContext db, ITenantContext tenant,
            CurrentUser me, MetaOAuthClient oauth, IOptions<MetaSocialOptions> options,
            CancellationToken ct) =>
        {
            if (!TryParseProvider(provider, out var metaProvider))
            {
                return Results.BadRequest(new { error = $"Unknown provider '{provider}'." });
            }
            if (!options.Value.IsConfigured(metaProvider))
            {
                // Not an error the admin can fix: the platform has no Meta app configured.
                return Results.Problem(
                    title: "Meta integration is not configured on this deployment.",
                    statusCode: StatusCodes.Status501NotImplemented);
            }

            // The token goes in the URL; only its hash is persisted, so a database leak does
            // not let anyone complete a pending consent.
            var stateToken = Base64Url(RandomNumberGenerator.GetBytes(32));

            db.OAuthStates.Add(new MetaOAuthState
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId!.Value,
                StateHash = Sha256Hex(stateToken),
                Provider = metaProvider,
                PluginInstanceId = instanceId,
                InitiatedBy = me.RequireUserId(),
                ReturnPath = options.Value.ReturnPath,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.Add(StateLifetime),
            });
            await db.SaveChangesAsync(ct);

            // JSON rather than a 302: the SPA calls this with a bearer token, and a redirect
            // on an XHR would be followed by fetch without ever reaching the address bar.
            return Results.Ok(new { authorizeUrl = oauth.BuildAuthorizeUrl(metaProvider, stateToken) });
        }).RequirePermission(PlatformPermissions.PluginsManage)
          .WithAudit(AuditActions.ConnectionStarted, "meta_connection");

        // ---------- callback ----------

        app.MapGet("/api/admin/social/callback", async (
            string? code, string? state, string? error, string? error_description,
            SocialDbContext db, MetaOAuthClient oauth, IServiceProvider services,
            IOptions<MetaSocialOptions> options, ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            var logger = loggers.CreateLogger("Dcms.AdminApi.Social.Callback");
            var returnPath = options.Value.ReturnPath;

            if (!string.IsNullOrEmpty(error))
            {
                // The admin declined, or Meta refused. Not an exception — send them back with
                // something the UI can render.
                logger.LogInformation("Meta consent returned error {Error}: {Description}", error, error_description);
                return Results.Redirect($"{returnPath}?social=denied");
            }
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            {
                return Results.BadRequest(new { error = "Missing code or state." });
            }

            // IgnoreQueryFilters is required and load-bearing: this request has no tenant
            // context, and this row is what establishes it.
            var hash = Sha256Hex(state);
            var pending = await db.OAuthStates.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.StateHash == hash, ct);

            if (pending is null || pending.ConsumedAt is not null || pending.ExpiresAt < DateTimeOffset.UtcNow)
            {
                // Deliberately one message for all three cases: distinguishing "unknown" from
                // "already used" from "expired" tells an attacker probing state values which
                // guess was closest.
                logger.LogWarning("Rejected Meta callback with an unusable state token.");
                return Results.BadRequest(new { error = "This connection link is no longer valid. Start again." });
            }

            pending.ConsumedAt = DateTimeOffset.UtcNow;

            try
            {
                var token = await oauth.ExchangeCodeAsync(pending.Provider, code, ct);
                var accounts = await oauth.DiscoverAccountsAsync(pending.Provider, token.AccessToken, ct);

                if (accounts.Count == 0)
                {
                    await db.SaveChangesAsync(ct);
                    return Results.Redirect($"{returnPath}?social=no-accounts");
                }

                // Resolved here rather than as a handler parameter on purpose. Minimal APIs
                // resolve parameters before the body runs, so injecting it would make an
                // unreachable Vault fail the request *before* the state check -- turning every
                // rejection of a forged or replayed token into a 500, and losing the cheap
                // validation that should happen first regardless of Vault's health.
                var encryptor = services.GetRequiredService<ITransitEncryptor>();

                var accessCiphertext = await encryptor.EncryptAsync(
                    VaultTransitServiceCollectionExtensions.SocialTokensKey,
                    Encoding.UTF8.GetBytes(token.AccessToken), ct);

                foreach (var account in accounts)
                {
                    var pageCiphertext = account.PageToken is { Length: > 0 } pageToken
                        ? await encryptor.EncryptAsync(
                            VaultTransitServiceCollectionExtensions.SocialTokensKey,
                            Encoding.UTF8.GetBytes(pageToken), ct)
                        : null;

                    // Reconnecting updates the existing row rather than adding one, so a
                    // re-consent replaces the token instead of leaving a stale one behind.
                    var existing = await db.Connections.IgnoreQueryFilters().FirstOrDefaultAsync(
                        c => c.TenantId == pending.TenantId
                             && c.Provider == account.Provider
                             && c.ExternalAccountId == account.ExternalAccountId, ct);

                    if (existing is null)
                    {
                        existing = new MetaConnection
                        {
                            Id = Guid.NewGuid(),
                            TenantId = pending.TenantId,
                            Provider = account.Provider,
                            ExternalAccountId = account.ExternalAccountId,
                            ConnectedBy = pending.InitiatedBy,
                            ConnectedAt = DateTimeOffset.UtcNow,
                        };
                        db.Connections.Add(existing);
                    }

                    existing.ExternalPageId = account.PageId;
                    existing.AccountName = account.Name;
                    existing.AccountUsername = account.Username;
                    existing.AvatarUrl = account.AvatarUrl;
                    existing.AccessTokenCiphertext = accessCiphertext;
                    existing.PageTokenCiphertext = pageCiphertext;
                    existing.TokenExpiresAt = token.ExpiresAt;
                    existing.ScopesGranted = string.Join(",", MetaScopes.For(pending.Provider));
                    existing.Status = MetaConnectionStatus.Active;
                    existing.LastError = null;
                    existing.LastRefreshedAt = DateTimeOffset.UtcNow;
                }

                await db.SaveChangesAsync(ct);
                return Results.Redirect($"{returnPath}?social=connected");
            }
            catch (MetaApiException ex)
            {
                // The state row stays consumed: a failed exchange must not leave a replayable
                // token behind, and the admin can simply start again.
                await db.SaveChangesAsync(ct);
                logger.LogError(ex, "Meta code exchange failed for tenant {TenantId}.", pending.TenantId);
                return Results.Redirect($"{returnPath}?social=failed");
            }
        }).AllowAnonymous().RequireRateLimiting(CallbackRateLimitPolicy);

        // ---------- list / disconnect ----------

        app.MapGet("/api/admin/social/connections", async (
            SocialDbContext db, CancellationToken ct) =>
        {
            var rows = await db.Connections.AsNoTracking()
                .OrderBy(c => c.AccountName)
                .ToListAsync(ct);

            // No ciphertext leaves this endpoint, in either direction. The UI only ever needs
            // to know that a credential exists and whether it still works.
            return Results.Ok(rows.Select(c => new
            {
                id = c.Id,
                provider = c.Provider.ToString(),
                accountName = c.AccountName,
                accountUsername = c.AccountUsername,
                avatarUrl = c.AvatarUrl,
                status = c.Status.ToString(),
                supportsStories = MetaScopes.SupportsStories(c.Provider),
                tokenExpiresAt = c.TokenExpiresAt,
                lastError = c.LastError,
                connectedAt = c.ConnectedAt,
            }));
        }).RequirePermission(PlatformPermissions.PluginsManage);

        app.MapPost("/api/admin/social/connections/{id:guid}/disconnect", async (
            Guid id, SocialDbContext db, CancellationToken ct) =>
        {
            var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (connection is null) return Results.NotFound();

            // Revoked rather than deleted: sync state rows point at this connection and an
            // admin asking "why did my feed stop" deserves an answer better than a missing row.
            // The tokens go now, though — that is the part that matters.
            connection.Status = MetaConnectionStatus.Revoked;
            connection.AccessTokenCiphertext = string.Empty;
            connection.PageTokenCiphertext = null;
            connection.TokenExpiresAt = null;
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.PluginsManage)
          .WithAudit(AuditActions.ConnectionRevoked, "meta_connection");

        // ---------- sync now ----------

        app.MapPost("/api/admin/social/instances/{instanceId:guid}/sync", async (
            Guid instanceId, CmsDbContext cms, MetaFeedSyncService sync,
            ITenantContext tenant, CancellationToken ct) =>
        {
            var instance = await cms.PluginInstances.FirstOrDefaultAsync(
                p => p.Id == instanceId && p.TenantId == tenant.TenantId, ct);

            if (instance is null) return Results.NotFound();

            // Deliberately the same code path the timer uses. A separate, simplified manual
            // sync is how the two drift until only one of them mirrors media.
            var outcome = await sync.SyncInstanceAsync(instance, ct);

            return outcome.Ok
                ? Results.Ok(new
                {
                    created = outcome.Created,
                    updated = outcome.Updated,
                    trimmed = outcome.Trimmed,
                    requests = outcome.PagesFetched,
                })
                : Results.BadRequest(new { error = outcome.Error });
        }).RequirePermission(PlatformPermissions.PluginsManage)
          .WithAudit(AuditActions.SocialSyncRequested, "plugin_instance");

        return app;
    }

    private static bool TryParseProvider(string value, out MetaProvider provider) =>
        Enum.TryParse(value.Replace("-", string.Empty), ignoreCase: true, out provider);

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
