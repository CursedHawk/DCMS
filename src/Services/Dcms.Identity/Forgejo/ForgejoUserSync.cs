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
        catch (ForgejoAdoptionRefusedException ex)
        {
            // Permanent, not transient: retrying cannot make an unconfirmed address confirmed,
            // and a queued row would just retry until it dead-letters. The user has no git
            // credentials until an operator reconciles the two accounts.
            _logger.LogError(ex, "Forgejo sync refused for {UserId}.", user.Id);
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
    /// Queue a sync instead of performing one, for callers on a latency-critical path.
    ///
    /// <para><b>Why this exists.</b> <see cref="EnsureAsync"/> tries Forgejo inline and only
    /// falls back to the outbox on failure. On registration and password reset that is right:
    /// they happen once, and the user is waiting for the account to exist anyway. On <i>login</i>
    /// it was not. Login called it on every sign-in as a self-heal, which meant an unconditional
    /// <c>PATCH /api/v1/admin/users/{name}</c> on the critical path of every authentication —
    /// measured at <b>421 ms of a 539 ms login</b>, 78% of the whole request, against about 9 ms
    /// of Postgres. Rewriting a password that had not changed, every time.</para>
    ///
    /// <para>This keeps the self-heal exactly as it was and only moves it off the request.
    /// <see cref="ForgejoSyncWorker"/> polls every 15 seconds and applies the row through the
    /// same <see cref="SyncInlineAsync"/> the inline path uses, so convergence is unchanged in
    /// substance and merely deferred by seconds — which is far inside the window before anyone
    /// uses the git credentials the sync maintains.</para>
    ///
    /// <para>Never throws: a failure to even queue is logged and swallowed, because nothing
    /// here is worth failing an authentication over.</para>
    /// </summary>
    public async Task DeferAsync(DcmsUser user, string? password, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(user.Email)) return;
        try
        {
            EnqueueAsync(user, password);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue deferred Forgejo sync for {UserId}.", user.Id);
        }
    }

    /// <summary>
    /// The sign-in self-heal: re-assert the mirror, but only when it could actually be wrong.
    ///
    /// <para><b>Why this is conditional.</b> Deferring the sync took the Forgejo <c>PATCH</c>
    /// off the login request, but it did not stop the write happening — it moved it to
    /// <see cref="ForgejoSyncWorker"/>, which fifteen seconds later rewrote a password that
    /// had not changed. Every sign-in still cost one admin write on the git server, plus the
    /// <c>INSERT</c> of a Data-Protection-encrypted password into the outbox on the request
    /// itself.</para>
    ///
    /// <para>None of that work was ever needed on the common path, because <b>every path that
    /// changes a credential already syncs inline</b>: register, password reset, and
    /// change-password all call <see cref="EnsureAsync"/> while the user waits, which is
    /// right — the account has to exist before they use it. What login adds is repair for a
    /// mirror that went wrong some other way: a provision that failed, a Forgejo restored
    /// from an older backup, an account edited on the git server directly. That is worth
    /// re-checking daily, not sixty times a day.</para>
    ///
    /// <para>So: sync when the mirror is missing, when it exists without a usable git
    /// password, or when the last confirmed sync is older than
    /// <see cref="ForgejoOptions.LoginResyncInterval"/>. Otherwise do nothing at all — no
    /// Forgejo call, and no database write on the authentication path.</para>
    /// </summary>
    public async Task DeferLoginResyncAsync(DcmsUser user, string password, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(user.Email)) return;
        if (!NeedsLoginResync(user)) return;
        await DeferAsync(user, password, ct);
    }

    private bool NeedsLoginResync(DcmsUser user) =>
        // Never mirrored, or mirrored without the git password this sign-in can supply.
        user.ForgejoUsername is null
        || !user.HasGitPassword
        // Mirrored by a build that predates the stamp, so its age is unknown: treat as due.
        || user.ForgejoSyncedAt is not { } syncedAt
        || DateTimeOffset.UtcNow - syncedAt >= _opts.LoginResyncInterval;

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
                RequireProvenEmail(user, existing.Login);
                user.ForgejoUsername = existing.Login;
                user.ForgejoUserId = existing.Id;
                await _admin.SetCredentialsAsync(existing.Login, email, password, ct);
            }
            else
            {
                var created = await CreateWithUniqueNameAsync(user, password, ct);
                user.ForgejoUsername = created.Login;
                user.ForgejoUserId = created.Id;
            }
        }
        else
        {
            // Safe without a further check: a stored mapping can only have been established
            // by a create below (an account this platform made) or by an adoption that
            // already passed RequireProvenEmail.
            await _admin.SetCredentialsAsync(user.ForgejoUsername, email, password, ct);
        }

        user.ForgejoSyncedAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrEmpty(password)) user.HasGitPassword = true;
    }

    /// <summary>
    /// Refuses to take over a Forgejo account this platform did not create unless the DCMS
    /// identity has proven the address.
    ///
    /// <para>Adoption sets the account's password. Registration is open, unauthenticated and
    /// does not confirm the address (<c>SignIn.RequireConfirmedAccount</c> is false), so
    /// without this anyone could register with the email of an existing Forgejo account —
    /// the headless-provisioned instance administrator being the obvious one — and have its
    /// password reset to whatever they typed into the form. That is admin on the git server,
    /// which is read/write on every tenant's site repository and a push to <c>release</c>
    /// away from deploying to their domains.</para>
    ///
    /// <para><c>EmailConfirmed</c> is the platform's only evidence that the address belongs to
    /// the person holding the session. Google SSO sets it (Google verified the address);
    /// password registration does not. So SSO users still adopt, which is what makes a lost
    /// mapping recoverable, and password users get a fresh account or a clear failure.</para>
    /// </summary>
    private static void RequireProvenEmail(DcmsUser user, string login)
    {
        if (user.EmailConfirmed) return;

        throw new ForgejoAdoptionRefusedException(
            $"A Forgejo account ({login}) already uses {user.Email}, and this identity has not " +
            "confirmed that address. Refusing to take over the account.");
    }

    private async Task<ForgejoUser> CreateWithUniqueNameAsync(DcmsUser user, string? password, CancellationToken ct)
    {
        var email = user.Email!;
        var baseName = DeriveUsername(email);
        for (var i = 0; i < 50; i++)
        {
            var candidate = i == 0 ? baseName : $"{baseName}-{i + 1}";
            var created = await _admin.CreateUserAsync(candidate, email, password, ct);
            if (created is not null) return created;

            // 422: either the email is taken (adopt it) or just the username (try next).
            var existing = await _admin.FindByEmailAsync(email, ct);
            if (existing is not null)
            {
                RequireProvenEmail(user, existing.Login);
                return existing;
            }
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
