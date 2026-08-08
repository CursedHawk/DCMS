namespace Dcms.Identity.Forgejo;

/// <summary>
/// A durable, retried Forgejo user-provisioning job. Written only when the inline
/// (fast-path) sync fails — e.g. Forgejo is temporarily unreachable — so a password
/// change is never lost and the two systems converge without blocking the user.
/// A background worker (<see cref="ForgejoSyncWorker"/>) drains it.
///
/// The new password (when present) is stored as Vault-Transit ciphertext in
/// <see cref="EncryptedPassword"/> — never plaintext — and the row is deleted once
/// applied.
/// </summary>
public sealed class ForgejoSyncOutbox
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The DCMS user this job provisions/updates.</summary>
    public Guid UserId { get; set; }

    /// <summary>Desired Forgejo username (derived at enqueue time; may be null for a pure credential update).</summary>
    public string? Username { get; set; }

    /// <summary>Desired email on the Forgejo account.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Vault-Transit ciphertext of the new password, or null when only email/account needs syncing.</summary>
    public string? EncryptedPassword { get; set; }

    public int Attempts { get; set; }

    /// <summary>Earliest time this row should be retried (exponential backoff).</summary>
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Last error, for diagnostics (never contains secrets).</summary>
    public string? LastError { get; set; }
}
