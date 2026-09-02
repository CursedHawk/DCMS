using System.Net;
using System.Text;
using Dcms.Shared.Audit;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Social;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Messaging.Email;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Social;

/// <summary>
/// Extends long-lived Meta tokens before they die, and tells somebody when one cannot be saved.
///
/// <para><b>Why this is not optional.</b> A Meta long-lived token lasts about sixty days and
/// cannot be renewed after it expires — there is no refresh token, only an exchange that
/// requires a token that still works. Miss the window and the only way back is an admin
/// repeating the whole consent flow. Without this worker every connected account silently
/// stops two months after it was connected, and the first anyone hears of it is a feed that
/// went quiet.</para>
///
/// <para>Daily is ample for a sixty-day credential, and the seven-day window means roughly a
/// week of chances to catch one: a single failed pass, a deploy, or a day of Meta trouble
/// costs nothing.</para>
///
/// <para>Same advisory-lock election as <see cref="MetaSyncWorker"/> — refreshing the same
/// token from two replicas at once is a race whose loser stores a token Meta has already
/// superseded.</para>
/// </summary>
public sealed class MetaTokenRefreshWorker(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<MetaTokenRefreshWorker> logger) : BackgroundService
{
    /// <summary>How close to expiry a token has to be before it is worth an exchange.</summary>
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromDays(7);

    private TimeSpan Interval =>
        TimeSpan.FromHours(configuration.GetValue("Social:TokenRefreshHours", 24));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Nothing here is urgent to the minute, and startup is already busy with migrations
        // and the RLS pass. Outbound HTTP in that window is how a deploy fails its own probe.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Meta token refresh pass failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunPassAsync(CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrEmpty(connectionString)) return;

        await using var lease = await PostgresAdvisoryLock.TryAcquireAsync(
            connectionString, PostgresAdvisoryLock.MetaTokenRefreshLockKey, logger, ct);

        if (lease is null) return;

        using var scope = services.CreateScope();
        var social = scope.ServiceProvider.GetRequiredService<SocialDbContext>();
        var oauth = scope.ServiceProvider.GetRequiredService<MetaOAuthClient>();
        var encryptor = scope.ServiceProvider.GetRequiredService<ITransitEncryptor>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();

        var deadline = DateTimeOffset.UtcNow.Add(RefreshWindow);

        // Cross-tenant: a timer has no ambient tenant. Only Active connections — one already
        // marked NeedsReauth has nothing left to refresh, and retrying it daily would be a
        // guaranteed-failing call plus a duplicate notification every morning.
        var due = await social.Connections.IgnoreQueryFilters()
            .Where(c => c.Status == MetaConnectionStatus.Active
                        && c.TokenExpiresAt != null
                        && c.TokenExpiresAt <= deadline)
            .ToListAsync(ct);

        foreach (var connection in due)
        {
            if (ct.IsCancellationRequested) break;
            await RefreshOneAsync(connection, social, oauth, encryptor, audit, ct);
        }

        if (due.Count > 0) await social.SaveChangesAsync(ct);
    }

    private async Task RefreshOneAsync(
        MetaConnection connection, SocialDbContext social, MetaOAuthClient oauth,
        ITransitEncryptor encryptor, IAuditRecorder audit, CancellationToken ct)
    {
        try
        {
            audit.Record(AuditActions.SecretAccessed)
                .InTenant(connection.TenantId)
                .For("meta_connection", connection.Id, connection.AccountName)
                .With("scope", "refresh");

            var current = Encoding.UTF8.GetString(await encryptor.DecryptAsync(
                VaultTransitServiceCollectionExtensions.SocialTokensKey,
                connection.AccessTokenCiphertext, ct));

            var refreshed = await oauth.RefreshAsync(connection.Provider, current, ct);

            connection.AccessTokenCiphertext = await encryptor.EncryptAsync(
                VaultTransitServiceCollectionExtensions.SocialTokensKey,
                Encoding.UTF8.GetBytes(refreshed.AccessToken), ct);
            connection.TokenExpiresAt = refreshed.ExpiresAt;
            connection.LastRefreshedAt = DateTimeOffset.UtcNow;
            connection.LastError = null;

            // Page tokens are deliberately left alone. A Page token derived from a long-lived
            // user token does not expire on its own, and there is no exchange for one — so
            // "refreshing" it would mean re-running discovery for no gain.
            logger.LogInformation(
                "Refreshed the Meta token for {Account}; now valid until {Expiry}.",
                connection.AccountName, refreshed.ExpiresAt);
        }
        catch (Exception ex)
        {
            // Any failure here is terminal for this credential: the exchange needs a token that
            // still works, and the one we hold is the one that is failing. Marking it now, while
            // there is still a week left, is what turns a silent outage into a warning.
            connection.Status = MetaConnectionStatus.NeedsReauth;
            connection.LastError = "The stored credential could not be renewed. Reconnect the account.";

            logger.LogWarning(ex,
                "Could not refresh the Meta token for connection {ConnectionId}; marking it for reauth.",
                connection.Id);

            await NotifyAsync(connection, ct);
        }
    }

    /// <summary>
    /// Emails the admin who connected the account. Best-effort by design: the connection is
    /// already flagged in the database and the widget will say so, so a mail failure must not
    /// throw away that state or stop the rest of the pass.
    /// </summary>
    private async Task NotifyAsync(MetaConnection connection, CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

            var recipient = await tenancy.Memberships.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.TenantId == connection.TenantId && m.UserId == connection.ConnectedBy)
                .Select(m => m.Email)
                .FirstOrDefaultAsync(ct);

            if (string.IsNullOrWhiteSpace(recipient))
            {
                // They left the workspace. Nothing to do but leave the flag for whoever looks.
                logger.LogInformation(
                    "No recipient for the reauth notice on connection {ConnectionId}.", connection.Id);
                return;
            }

            var email = scope.ServiceProvider.GetRequiredService<IEmailQueue>();

            // The account name is whatever the Page is called on Meta, so it is third-party
            // text arriving from an API — not ours, and not necessarily plain. Escaped for the
            // HTML body, the same way every other email producer here does it.
            //
            // The subject stays raw on purpose: it becomes MimeMessage.Subject, a plain-text
            // header that MimeKit encodes itself, so escaping it would only put a literal
            // "&amp;" in front of the reader.
            var safeName = Enc(connection.AccountName);

            await email.EnqueueAsync(new EmailMessage(
                [recipient],
                $"Reconnect {connection.AccountName} to keep your feed running",
                $"""
                 <p>The connection to <strong>{safeName}</strong> can no longer be renewed,
                 and Meta will stop accepting it shortly.</p>
                 <p>Open <em>Plugins</em> in your DCMS admin and choose <strong>Reconnect</strong> on that
                 account. Nothing already published will be lost — new posts simply stop arriving until
                 the connection is restored.</p>
                 """,
                Purpose: "social-reauth",
                TenantId: connection.TenantId,
                // One notice per connection per day, not one per pass: the worker will find this
                // connection again tomorrow, and a daily reminder is a nudge while a stream of
                // duplicates is something people filter out.
                DedupeKey: $"social-reauth:{connection.Id:N}:{DateTimeOffset.UtcNow:yyyyMMdd}"), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not queue the reauth notice for connection {ConnectionId}.", connection.Id);
        }
    }

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
