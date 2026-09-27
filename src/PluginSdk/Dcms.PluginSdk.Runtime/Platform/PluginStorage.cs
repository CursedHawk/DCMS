using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;

namespace Dcms.PluginSdk.Runtime.Platform;

/// <summary>
/// <see cref="IPluginStorage"/> over <c>plugins.plugin_data</c>. Tenant and plugin id come from
/// the caller's <see cref="IPluginContext"/> and are never read from input, so there is no
/// argument a plugin can pass to reach another plugin's or tenant's documents.
/// </summary>
public sealed class PluginStorage(IPluginContext caller, CmsDbContext db) : IPluginStorage
{
    public const int MaxDocumentBytes = 256 * 1024;
    public const int MaxPageSize = 100;

    public async Task<StoredDocument?> GetAsync(DocumentAddress input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(caller.TenantId);
        var instanceId = InstanceFor(input.InstanceScoped);
        Validate(input.Collection, input.Key);
        var row = await Mine(instanceId).AsNoTracking()
            .FirstOrDefaultAsync(d => d.Collection == input.Collection && d.Key == input.Key, ct);
        return row is null ? null : ToDocument(row);
    }

    public async Task<StoredDocument> PutAsync(PutDocument input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(caller.TenantId);
        var instanceId = InstanceFor(input.InstanceScoped);
        Validate(input.Collection, input.Key);
        var json = input.Data.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes)
        {
            throw new ContractValidationException($"Document exceeds {MaxDocumentBytes / 1024} KiB.");
        }

        var row = await Mine(instanceId)
            .FirstOrDefaultAsync(d => d.Collection == input.Collection && d.Key == input.Key, ct);
        var now = DateTimeOffset.UtcNow;

        if (row is null)
        {
            if (input.ExpectedVersion is > 0)
            {
                throw new ContractConflictException($"'{input.Collection}/{input.Key}' does not exist.");
            }
            row = new PluginDatum
            {
                Id = Guid.CreateVersion7(),
                TenantId = caller.TenantId,
                PluginId = caller.PluginId,
                InstanceId = instanceId,
                Collection = input.Collection,
                Key = input.Key,
                DataJson = json,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.PluginData.Add(row);
        }
        else
        {
            if (input.ExpectedVersion is { } expected && expected != row.Version)
            {
                throw new ContractConflictException(
                    $"'{input.Collection}/{input.Key}' is at version {row.Version}, not {expected}.");
            }
            row.DataJson = json;
            row.Version++;
            row.UpdatedAt = now;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e is DbUpdateConcurrencyException || IsUniqueViolation(e))
        {
            db.ChangeTracker.Clear();
            throw new ContractConflictException($"'{input.Collection}/{input.Key}' was written concurrently.");
        }
        return ToDocument(row);
    }

    public async Task<DeleteResult> DeleteAsync(DocumentAddress input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(caller.TenantId);
        var instanceId = InstanceFor(input.InstanceScoped);
        Validate(input.Collection, input.Key);
        // One row by its unique key, so a tracked delete; the contract proxy records the call.
        var row = await Mine(instanceId)
            .FirstOrDefaultAsync(d => d.Collection == input.Collection && d.Key == input.Key, ct);
        if (row is null)
        {
            return new DeleteResult(false);
        }
        db.PluginData.Remove(row);
        await db.SaveChangesAsync(ct);
        return new DeleteResult(true);
    }

    public async Task<DocumentPage> QueryAsync(QueryDocuments input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(caller.TenantId);
        var instanceId = InstanceFor(input.InstanceScoped);
        Validate(input.Collection, key: null);
        var page = Math.Max(input.Page, 1);
        var size = Math.Clamp(input.PageSize, 1, MaxPageSize);

        var query = Mine(instanceId).AsNoTracking().Where(d => d.Collection == input.Collection);
        if (input.Where is { Count: > 0 } where)
        {
            var filter = JsonSerializer.Serialize(where);
            query = query.Where(d => EF.Functions.JsonContains(d.DataJson, filter));
        }

        var total = await query.LongCountAsync(ct);
        var rows = await query
            .OrderBy(d => d.Key)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);
        return new DocumentPage(rows.Select(ToDocument).ToList(), page, size, total);
    }

    private IQueryable<PluginDatum> Mine(Guid? instanceId) =>
        db.PluginData.IgnoreQueryFilters().Where(d =>
            d.TenantId == caller.TenantId && d.PluginId == caller.PluginId && d.InstanceId == instanceId);

    private Guid? InstanceFor(bool instanceScoped)
    {
        if (!instanceScoped)
        {
            return null;
        }
        return caller.Instance?.InstanceId
            ?? throw new ContractValidationException(
                "No instance in this context (a job or event handler); use InstanceScoped = false for plugin-wide data.");
    }

    private static void Validate(string collection, string? key)
    {
        if (string.IsNullOrWhiteSpace(collection) || collection.Length > 64)
        {
            throw new ContractValidationException("Collection must be 1-64 characters.");
        }
        if (key is not null && (string.IsNullOrWhiteSpace(key) || key.Length > 256))
        {
            throw new ContractValidationException("Key must be 1-256 characters.");
        }
    }

    private static StoredDocument ToDocument(PluginDatum row)
    {
        using var doc = JsonDocument.Parse(row.DataJson);
        return new StoredDocument(row.Collection, row.Key, doc.RootElement.Clone(), row.Version, row.UpdatedAt);
    }

    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };
}
