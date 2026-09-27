using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.PluginSdk.Abstractions.Platform;

/// <param name="InstanceId">Required only outside an instance (a job or event handler); must be the caller's own instance.</param>
public sealed record SecretName(string Name, Guid? InstanceId = null);

public sealed record SetSecret(string Name, string Value, Guid? InstanceId = null);

public sealed record SecretValue(string? Value);

public sealed record SecretNames(Guid? InstanceId = null);

public sealed record SecretList(IReadOnlyList<string> Names);

/// <summary>
/// Credentials for one of the calling plugin's instances, Vault-Transit-encrypted at rest.
/// Credentials never go in instance config (config is readable by admins and partly public).
///
/// <para><b>Available in admin-api only.</b> content-api is internet-facing and holds no key
/// to decrypt with, so this contract does not resolve there: code that spends a credential
/// belongs on an admin route, a job or an event handler.</para>
/// </summary>
[DcmsContract("dcms.secrets", 1, Description = "Encrypted credentials for the calling plugin's instances (admin plane only).")]
public interface IPluginSecrets
{
    [Operation(OpRisk.Read)]
    Task<SecretValue> GetAsync(SecretName input, CancellationToken ct);

    [Operation(OpRisk.Safe)]
    Task SetAsync(SetSecret input, CancellationToken ct);

    [Operation(OpRisk.Safe)]
    Task<DeleteResult> DeleteAsync(SecretName input, CancellationToken ct);

    /// <summary>Names only; values are never listed.</summary>
    [Operation(OpRisk.Read)]
    Task<SecretList> ListAsync(SecretNames input, CancellationToken ct);
}
