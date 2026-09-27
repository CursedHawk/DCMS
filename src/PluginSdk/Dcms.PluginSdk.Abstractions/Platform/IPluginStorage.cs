using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.PluginSdk.Abstractions.Platform;

/// <summary>A document address. <paramref name="InstanceScoped"/> false addresses plugin-wide data.</summary>
public sealed record DocumentAddress(string Collection, string Key, bool InstanceScoped = true);

/// <param name="ExpectedVersion">
/// Optimistic concurrency: the version last read, or 0 for "must not exist yet". Null writes
/// unconditionally.
/// </param>
public sealed record PutDocument(
    string Collection, string Key, JsonElement Data, int? ExpectedVersion = null, bool InstanceScoped = true);

/// <param name="Where">Top-level equality filters, matched with jsonb containment.</param>
public sealed record QueryDocuments(
    string Collection,
    IReadOnlyDictionary<string, JsonElement>? Where = null,
    int Page = 1,
    int PageSize = 50,
    bool InstanceScoped = true);

public sealed record StoredDocument(string Collection, string Key, JsonElement Data, int Version, DateTimeOffset UpdatedAt);

public sealed record DocumentPage(IReadOnlyList<StoredDocument> Items, int Page, int PageSize, long TotalCount);

public sealed record DeleteResult(bool Found);

/// <summary>
/// A plugin's private document store: JSON values by collection and key, per instance or
/// plugin-wide. The platform stamps the tenant and the calling plugin onto every row, so a
/// plugin sees only its own documents — this is what spares it a table, a migration and an RLS
/// entry of its own.
/// </summary>
[DcmsContract("dcms.storage", 1, Description = "Private JSON document store for the calling plugin.")]
public interface IPluginStorage
{
    [Operation(OpRisk.Read)]
    Task<StoredDocument?> GetAsync(DocumentAddress input, CancellationToken ct);

    /// <exception cref="ContractConflictException">ExpectedVersion did not match.</exception>
    [Operation(OpRisk.Safe)]
    Task<StoredDocument> PutAsync(PutDocument input, CancellationToken ct);

    [Operation(OpRisk.Safe)]
    Task<DeleteResult> DeleteAsync(DocumentAddress input, CancellationToken ct);

    [Operation(OpRisk.Read)]
    Task<DocumentPage> QueryAsync(QueryDocuments input, CancellationToken ct);
}
