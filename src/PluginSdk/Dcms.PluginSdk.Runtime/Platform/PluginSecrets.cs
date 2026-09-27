using System.Text;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;

namespace Dcms.PluginSdk.Runtime.Platform;

/// <summary>
/// <see cref="IPluginSecrets"/> over <c>plugins.plugin_secrets</c>, Transit key
/// <see cref="KeyName"/>. Registered in admin-api only (<c>AddPluginSecrets</c>): Vault grants
/// that key to admin-api alone, so the contract simply does not exist on the public plane.
/// </summary>
public sealed class PluginSecrets(IPluginContext caller, CmsDbContext db, ITransitEncryptor transit) : IPluginSecrets
{
    public const string KeyName = "dcms-plugin-secrets";
    private const int MaxValueBytes = 16 * 1024;

    public async Task<SecretValue> GetAsync(SecretName input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(caller.TenantId);
        var instanceId = await InstanceAsync(input.InstanceId, ct);
        Validate(input.Name);
        var row = await Mine(instanceId).AsNoTracking().FirstOrDefaultAsync(s => s.Name == input.Name, ct);
        if (row is null)
        {
            return new SecretValue(null);
        }
        var plain = await transit.DecryptAsync(KeyName, row.Ciphertext, ct);
        return new SecretValue(Encoding.UTF8.GetString(plain));
    }

    public async Task SetAsync(SetSecret input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(caller.TenantId);
        var instanceId = await InstanceAsync(input.InstanceId, ct);
        Validate(input.Name);
        var bytes = Encoding.UTF8.GetBytes(input.Value ?? string.Empty);
        if (bytes.Length is 0 or > MaxValueBytes)
        {
            throw new ContractValidationException($"Secret value must be 1 byte to {MaxValueBytes / 1024} KiB.");
        }
        var ciphertext = await transit.EncryptAsync(KeyName, bytes, ct);

        var row = await Mine(instanceId).FirstOrDefaultAsync(s => s.Name == input.Name, ct);
        if (row is null)
        {
            db.PluginSecrets.Add(new PluginSecret
            {
                Id = Guid.CreateVersion7(),
                TenantId = caller.TenantId,
                PluginId = caller.PluginId,
                InstanceId = instanceId,
                Name = input.Name,
                Ciphertext = ciphertext,
            });
        }
        else
        {
            row.Ciphertext = ciphertext;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<DeleteResult> DeleteAsync(SecretName input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(caller.TenantId);
        var instanceId = await InstanceAsync(input.InstanceId, ct);
        Validate(input.Name);
        var row = await Mine(instanceId).FirstOrDefaultAsync(s => s.Name == input.Name, ct);
        if (row is null)
        {
            return new DeleteResult(false);
        }
        db.PluginSecrets.Remove(row);
        await db.SaveChangesAsync(ct);
        return new DeleteResult(true);
    }

    public async Task<SecretList> ListAsync(SecretNames input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(caller.TenantId);
        var instanceId = await InstanceAsync(input.InstanceId, ct);
        var names = await Mine(instanceId).AsNoTracking().OrderBy(s => s.Name).Select(s => s.Name).ToListAsync(ct);
        return new SecretList(names);
    }

    private IQueryable<PluginSecret> Mine(Guid instanceId) =>
        db.PluginSecrets.IgnoreQueryFilters().Where(s =>
            s.TenantId == caller.TenantId && s.PluginId == caller.PluginId && s.InstanceId == instanceId);

    /// <summary>
    /// The instance being served, or — outside one — an explicitly named instance, which must
    /// belong to the calling plugin in the calling tenant.
    /// </summary>
    private async Task<Guid> InstanceAsync(Guid? requested, CancellationToken ct)
    {
        if (caller.Instance is { } own)
        {
            if (requested is { } other && other != own.InstanceId)
            {
                throw new ContractValidationException("A secret can only be addressed on the instance being served.");
            }
            return own.InstanceId;
        }
        if (requested is not { } id)
        {
            throw new ContractValidationException("No instance in this context; pass InstanceId.");
        }
        var owned = await db.PluginInstances.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(p => p.Id == id && p.TenantId == caller.TenantId && p.PluginId == caller.PluginId, ct);
        return owned ? id : throw new ContractValidationException("Unknown instance for this plugin.");
    }

    private static void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            throw new ContractValidationException("Secret name must be 1-128 characters.");
        }
    }
}
