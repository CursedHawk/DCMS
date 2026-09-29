using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace Dcms.PluginSdk.Runtime.Platform;

/// <summary>
/// <see cref="IPluginSearch"/> over <c>search.search_documents</c> (Postgres full text,
/// <c>websearch_to_tsquery</c>). Scoped by the caller's tenant explicitly, since callers such as
/// the chatbot run with no request behind them.
/// </summary>
public sealed class PluginSearch(IPluginContext caller, SearchDbContext search) : IPluginSearch
{
    public const int MaxLimit = 50;
    public const int MaxBodyChars = 2000;

    public async Task<SearchResults> SearchAsync(SearchRequest input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Query))
        {
            return new SearchResults([], input.IncludeTotal ? 0 : null);
        }
        if (input.Query.Length > 500)
        {
            throw new ContractValidationException("Query is at most 500 characters.");
        }
        var q = input.Query;
        var take = Math.Clamp(input.Limit, 1, MaxLimit);
        var bodyChars = Math.Clamp(input.BodyChars, 0, MaxBodyChars);

        using var rls = RlsScope.Tenant(caller.TenantId);
        // EF.Functions.* must stay inside the expression (it throws if invoked directly), so the
        // tsquery is rebuilt inline rather than hoisted to a local.
        var query = search.Documents.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == caller.TenantId
                        && d.SearchVector.Matches(EF.Functions.WebSearchToTsQuery("simple", q)));

        long? total = input.IncludeTotal ? await query.LongCountAsync(ct) : null;
        var rows = await query
            .OrderByDescending(d => d.SearchVector.Rank(EF.Functions.WebSearchToTsQuery("simple", q)))
            .Take(take)
            .Select(d => new { d.Title, d.Url, d.ContentType, d.Body })
            .ToListAsync(ct);

        return new SearchResults(
            rows.Select(r => new SearchHit(
                    r.Title, r.Url, r.ContentType,
                    bodyChars == 0 ? null : r.Body.Length > bodyChars ? r.Body[..bodyChars] + "…" : r.Body))
                .ToList(),
            total);
    }
}
