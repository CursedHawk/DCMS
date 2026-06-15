using DnsClient;

namespace Dcms.AdminApi.Tenancy;

/// <summary>Resolves TXT records for domain-ownership verification.</summary>
public interface IDnsTxtLookup
{
    Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken ct = default);
}

public sealed class DnsTxtLookup : IDnsTxtLookup
{
    private readonly LookupClient _client = new();

    public async Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken ct = default)
    {
        var result = await _client.QueryAsync(name, QueryType.TXT, cancellationToken: ct);
        return result.Answers.TxtRecords()
            .SelectMany(r => r.Text)
            .ToList();
    }
}
