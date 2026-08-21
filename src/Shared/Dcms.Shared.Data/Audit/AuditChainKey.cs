using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Data.Audit;

/// <summary>Bound from the "Audit" configuration section, which Vault overlays in deployed environments.</summary>
public sealed class AuditOptions
{
    public const string SectionName = "Audit";

    /// <summary>
    /// Base64 HMAC key for the chain. Supplied from Vault (<c>secret/dcms/admin-api</c>) in any
    /// real environment; absent in local development, where a fixed development key is used.
    /// </summary>
    public string? ChainKey { get; set; }

    /// <summary>Records are kept this long before their partition is dropped.</summary>
    public int RetentionDays { get; set; } = 400;

    // There is deliberately no "Enabled" switch. Recording is not a feature to be turned off:
    // an environment where it can be disabled is an environment where someone will disable it
    // and forget. The test suite runs with audit on, which is the point — it exercises the
    // real write path rather than a parallel one that only tests use.
}

/// <summary>
/// Supplies the chain's HMAC key.
///
/// <para>The key must be <b>stable across restarts</b> — a freshly generated key would make
/// every previously written record unverifiable, which reads exactly like tampering. So a
/// missing key is not quietly replaced with a random one: outside development it is a startup
/// failure, because a chain nobody can verify is worse than an obviously absent one.</para>
/// </summary>
public interface IAuditChainKeyProvider
{
    byte[] GetKey();
}

public sealed class AuditChainKeyProvider : IAuditChainKeyProvider
{
    // Constant, and constant on purpose: a development chain has to survive `docker compose
    // restart` to be usable at all. It grants nothing — the deployed key comes from Vault.
    private const string DevelopmentKey = "dcms-development-audit-chain-key-do-not-use-in-production";

    private readonly byte[] _key;

    public AuditChainKeyProvider(AuditOptions options, bool isDevelopment, ILogger<AuditChainKeyProvider> logger)
    {
        if (!string.IsNullOrWhiteSpace(options.ChainKey))
        {
            _key = Convert.FromBase64String(options.ChainKey);
            if (_key.Length < 32)
            {
                throw new InvalidOperationException(
                    "Audit:ChainKey must be at least 32 bytes; a short key weakens the only control that survives database compromise.");
            }
            return;
        }

        if (!isDevelopment)
        {
            throw new InvalidOperationException(
                "Audit:ChainKey is not configured. Provision it in Vault under secret/dcms/admin-api — "
                + "without it the audit chain cannot be verified, and an unverifiable chain is indistinguishable from a tampered one.");
        }

        logger.LogWarning(
            "Audit:ChainKey is not configured; using the built-in development key. The chain is NOT tamper-evident in this environment.");
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(DevelopmentKey));
    }

    public byte[] GetKey() => _key;
}
