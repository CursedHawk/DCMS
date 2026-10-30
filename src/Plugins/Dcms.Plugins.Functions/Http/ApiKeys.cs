using System.Security.Cryptography;
using System.Text;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Data.Functions;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.Functions.Http;

public sealed record ApiKeyInfo(Guid Id, string Name, string Prefix, IReadOnlyList<string> Functions, DateTimeOffset CreatedAt, string? CreatedBy);

/// <param name="Secret">The key itself: shown this once, and never again.</param>
public sealed record ApiKeyCreated(ApiKeyInfo Key, string Secret);

public sealed record CreateApiKey(string Name, List<string>? Functions = null);

/// <summary>
/// An instance's API keys in this environment (ADR 0029): what outside callers present in
/// <c>X-Api-Key</c> to invoke functions whose access names <c>apiKey</c>. Only a SHA-256 of
/// each key is stored; it is found by its prefix and compared in constant time.
/// </summary>
public sealed class ApiKeyService(FunctionsDbContext db, IPluginContext context)
{
    public const string Header = "X-Api-Key";
    public const int MaxKeys = 100;
    private const string Marker = "dfk_";
    private const int PrefixLength = 12;

    private Guid InstanceId => context.Instance?.InstanceId ?? throw new InvalidOperationException("No instance.");

    public async Task<IReadOnlyList<ApiKeyInfo>> ListAsync(CancellationToken ct)
    {
        using var rls = Scope();
        return (await db.ApiKeys.AsNoTracking().Where(k => k.InstanceId == InstanceId && k.RevokedAt == null)
                .OrderByDescending(k => k.CreatedAt).ToListAsync(ct))
            .Select(Info).ToList();
    }

    public async Task<ApiKeyCreated> CreateAsync(CreateApiKey input, CancellationToken ct)
    {
        var name = input.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100)
        {
            throw new ContractValidationException("A key needs a name of 1 to 100 characters.");
        }
        var functions = (input.Functions ?? []).Select(f => f.Trim()).Where(f => f.Length > 0).Distinct().ToList();
        if (functions.Count > 50 || functions.Any(f => f.Length > 64))
        {
            throw new ContractValidationException("A key names at most 50 functions.");
        }
        using var rls = Scope();
        if (await db.ApiKeys.CountAsync(k => k.InstanceId == InstanceId && k.RevokedAt == null, ct) >= MaxKeys)
        {
            throw new ContractConflictException($"An app has at most {MaxKeys} keys. Revoke one first.");
        }
        var secret = Marker + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var key = new FunctionApiKey
        {
            Id = Guid.CreateVersion7(),
            TenantId = context.TenantId,
            EnvironmentId = context.EnvironmentId,
            InstanceId = InstanceId,
            Name = name,
            Prefix = secret[..PrefixLength],
            Hash = Hash(secret),
            Functions = functions,
            CreatedBy = context.Actor.Id?.ToString() ?? context.Actor.Key,
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        return new ApiKeyCreated(Info(key), secret);
    }

    /// <summary>False when there was no such live key.</summary>
    public async Task<bool> RevokeAsync(Guid id, CancellationToken ct)
    {
        using var rls = Scope();
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.InstanceId == InstanceId && k.RevokedAt == null, ct);
        if (key is null) return false;
        key.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="secret"/> is a live key of the instance, in the environment in
    /// scope, that may invoke <paramref name="function"/>.
    /// </summary>
    public static async Task<bool> VerifyAsync(FunctionsDbContext db, Guid instanceId, string? secret, string function, CancellationToken ct)
    {
        if (secret is not { Length: > PrefixLength and <= 200 } || !secret.StartsWith(Marker, StringComparison.Ordinal))
        {
            return false;
        }
        var prefix = secret[..PrefixLength];
        var key = await db.ApiKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Prefix == prefix && k.InstanceId == instanceId && k.RevokedAt == null, ct);
        return key is not null
               && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(key.Hash), SHA256.HashData(Encoding.UTF8.GetBytes(secret)))
               && (key.Functions.Count == 0 || key.Functions.Contains(function));
    }

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static ApiKeyInfo Info(FunctionApiKey k) => new(k.Id, k.Name, k.Prefix, k.Functions, k.CreatedAt, k.CreatedBy);

    private IDisposable Scope() => RlsScope.Environment(context.TenantId, context.EnvironmentId);
}
