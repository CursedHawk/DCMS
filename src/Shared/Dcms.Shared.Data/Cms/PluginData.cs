using Dcms.Shared.Audit.Redaction;
using Dcms.Shared.Data.Sandbox;

namespace Dcms.Shared.Data.Cms;

/// <summary>
/// One document in a plugin's private store (the <c>dcms.storage@1</c> platform contract): a
/// JSON value addressed by (plugin, instance, collection, key). This is what spares a plugin a
/// table, a migration and an RLS entry of its own.
///
/// <para><see cref="PluginId"/> is stamped by the runtime from the calling plugin's context and
/// never taken from the caller, so one plugin cannot read another's documents in the same
/// tenant. Not entity-audited: the contract proxy already records every write as a call.</para>
/// </summary>
[AuditIgnore]
public sealed class PluginDatum : TenantEntity, ISandboxScoped
{
    public string PluginId { get; set; } = string.Empty;

    /// <summary>
    /// Written from a site preview (<c>X-Dcms-Sandbox</c>). Preview documents are a separate
    /// store: a plugin exercised in a preview must never read or overwrite live data.
    /// </summary>
    public bool IsSandbox { get; set; }

    /// <summary>The owning instance, or null for plugin-wide data.</summary>
    public Guid? InstanceId { get; set; }

    public string Collection { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string DataJson { get; set; } = "{}";

    /// <summary>Optimistic concurrency: a write names the version it read.</summary>
    public int Version { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A named credential held for a plugin instance (the <c>dcms.secrets@1</c> platform contract),
/// Vault-Transit-encrypted under <c>dcms-plugin-secrets</c>. Only admin-api holds that key:
/// content-api is internet-facing and must never be able to decrypt a tenant credential.
/// </summary>
[AuditIgnore]
public sealed class PluginSecret : TenantEntity
{
    public string PluginId { get; set; } = string.Empty;
    public Guid InstanceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Ciphertext { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
