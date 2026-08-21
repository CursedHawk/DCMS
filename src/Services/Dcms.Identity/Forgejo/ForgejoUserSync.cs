using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Propagation;
using System.Text.RegularExpressions;
using Dcms.Identity.Data;
using Dcms.Identity.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Dcms.Identity.Forgejo;

/// <summary>
/// Mirrors a DCMS identity into a Forgejo account and keeps the login (email +
/// password) in sync, so users clone/pull/push the site repos with their own
/// credentials. Call <see cref="EnsureAsync"/> wherever a plaintext password is
/// available (register / reset / login) or an account must exist (Google signup).
///
/// Reliability model ("perfect sync" without blocking the user): try the Forgejo
/// call inline (fast path); if it fails — e.g. Forgejo is momentarily down — persist
/// a durable, retried outbox row (password stored as Vault-Transit ciphertext, never
/// plaintext) that <see cref="ForgejoSyncWorker"/> drains until it converges.
/// </summary>
public sealed partial class ForgejoUserSync
{
    private readonly ForgejoAdminClient _admin;
    private readonly IdentityDbContext _db;
    private readonly IDataProtector _protector;
    private readonly ForgejoOptions _opts;
    private readonly AuditScope _scope;
    private readonly ILogger<ForgejoUserSync> _logger;

    public ForgejoUserSync(
        ForgejoAdminClient admin,
        IdentityDbContext db,
        IDataProtectionProvider dataProtection,
        IOptions<ForgejoOptions> options,
        AuditScope scope,
        ILogger<ForgejoUserSync> logger)
    {
        _admin = admin;
        _db = db;
        // Encrypt outbox passwords at rest with Data Protection (self-contained; no
        // Vault dependency — Vault's token can't do transit on this host).
        _protector = dataProtection.CreateProtector("Dcms.Identity.Forgejo.OutboxPassword.v1");
        _opts = options.Value;
        _scope = scope;
        _logger = logger;
    }

    public bool Enabled => _opts.Enabled;

    /// <summary>Ensure the user has a linked Forgejo account (creating one with no
    /// password if needed) and return its username, or null if it couldn't be
    /// established (e.g. Forgejo unreachable). Used by account/SSH-key flows.</summary>
    public async Task<string?> EnsureAccountAsync(DcmsUser user, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(user.Email)) return null;
        if (user.ForgejoUsername is not null) return user.ForgejoUsername;
        await EnsureAsync(user, password: null, ct);
        return user.ForgejoUsername;
    }

    /// <summary>
    /// Ensure <paramref name="user"/> has a linked Forgejo account and that its email
    /// (and password, when supplied) match. Never throws — a Forgejo failure degrades
    /// to a durable retry so the caller's primary flow is unaffected.
    /// </summary>
    public async Task EnsureAsync(DcmsUser user, string? password, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(user.Email)) return;
        try
        {
            await SyncInlineAsync(user, password, ct);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Inline Forgejo sync failed for {UserId}; enqueuing durable retry.", user.Id);
            try
            {
                EnqueueAsync(user, password);
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex2)
            {
                // Even enqueue failed (DB down): last resort — self-heal happens on next
                // login. Log loudly; do not throw into the auth flow.
                _logger.LogError(ex2, "Failed to enqueue Forgejo sync for {UserId}.", user.Id);
            }
        }
    }

    /// <summary>
    /// Apply one outbox row (called by <see cref="ForgejoSyncWorker"/>): decrypt the
    /// password, run the same sync as the inline path, and persist. Throws on failure
    /// so the worker can back off and retry; the row is left for the caller to manage.
    /// </summary>
    public async Task ApplyOutboxAsync(ForgejoSyncOutbox row, CancellationToken ct)
    {
        var user = await _db.Users.FindAsync([row.UserId], ct);
        if (user is null) return; // user was deleted; nothing to sync (caller drops the row)

        var password = row.EncryptedPassword is null ? null : _protector.Unprotect(row.EncryptedPassword);

        await SyncInlineAsync(user, password, ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task SyncInlineAsync(DcmsUser user, string? password, CancellationToken ct)
    {
        var email = user.Email!;
        if (user.ForgejoUsername is null)
        {
            // Adopt an existing account with this email (out-of-band or a lost mapping),
            // otherwise create a fresh one with a unique handle.
            var existing = await _admin.FindByEmailAsync(email, ct);
            if (existing is not null)
            {
                user.ForgejoUsername = existing.Login;
                user.ForgejoUserId = existing.Id;
                await _admin.SetCredentialsAsync(existing.Login, email, password, ct);
            }
            else
            {
                var created = await CreateWithUniqueNameAsync(email, password, ct);
                user.ForgejoUsername = created.Login;
                user.ForgejoUserId = created.Id;
            }
        }
        else
        {
            await _admin.SetCredentialsAsync(user.ForgejoUsername, email, password, ct);
        }

        user.ForgejoSyncedAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrEmpty(password)) user.HasGitPassword = true;
    }

    private async Task<ForgejoUser> CreateWithUniqueNameAsync(string email, string? password, CancellationToken ct)
    {
        var baseName = DeriveUsername(email);
        for (var i = 0; i < 50; i++)
        {
            var candidate = i == 0 ? baseName : $"{baseName}-{i + 1}";
            var created = await _admin.CreateUserAsync(candidate, email, password, ct);
            if (created is not null) return created;

            // 422: either the email is taken (adopt it) or just the username (try next).
            var existing = await _admin.FindByEmailAsync(email, ct);
            if (existing is not null) return existing;
        }
        throw new InvalidOperationException($"Could not allocate a Forgejo username for {email}.");
    }

    private void EnqueueAsync(DcmsUser user, string? password)
    {
        var encrypted = string.IsNullOrEmpty(password) ? null : _protector.Protect(password);
        _db.ForgejoSyncOutbox.Add(new ForgejoSyncOutbox
        {
            UserId = user.Id,
            Username = user.ForgejoUsername ?? DeriveUsername(user.Email!),
            Email = user.Email!,
            EncryptedPassword = encrypted,
            // Whoever changed the credential, remembered across the retry backoff — which
            // reaches an hour, by which point nothing else remembers the request at all.
            ContextJson = AuditPropagation.CaptureJson(_scope),
        });
    }

    /// <summary>Derive a valid Forgejo username from an email's local part: keep only
    /// [A-Za-z0-9-_.], collapse runs, trim leading/trailing punctuation, lowercase, cap
    /// length. Falls back to "user" if nothing usable remains.</summary>
    internal static string DeriveUsername(string email)
    {
        var local = email.Split('@', 2)[0];
        var cleaned = InvalidChars().Replace(local, "-").Trim('-', '.', '_').ToLowerInvariant();
        if (cleaned.Length > 30) cleaned = cleaned[..30].Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(cleaned) ? "user" : cleaned;
    }

    [GeneratedRegex("[^A-Za-z0-9-_.]+")]
    private static partial Regex InvalidChars();
}
