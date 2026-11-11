using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Data;
using Dcms.Plugins.VisitorAuth.Api;
using Dcms.Shared.Data.Visitors;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.VisitorAuth;

/// <summary><c>visitors.accounts@1</c>: the admin page's visitor actions, through the same data set the page uses.</summary>
public sealed class VisitorAccounts(IPluginContext context, VisitorsDbContext db, ILogger<VisitorsDataSet> logger) : IVisitorAccounts
{
    private VisitorsDataSet Visitors => new(context, db, logger);

    public async Task<VisitorAccountPage> FindAsync(VisitorSearch input, CancellationToken ct)
    {
        var page = await Visitors.ListAsync(new DataQuery(string.IsNullOrWhiteSpace(input.Search) ? null : input.Search.Trim(), "createdAt", true,
            new Dictionary<string, string>(), Math.Max(input.Page, 1), Math.Clamp(input.PageSize, 1, 100)), ct);
        return new VisitorAccountPage(page.Rows.Select(r => new VisitorAccountInfo(
            Guid.Parse(r.Key), r.Values["email"]!.GetValue<string>(), r.Values["displayName"]?.GetValue<string>(),
            r.Values["emailVerified"]!.GetValue<bool>(), r.Values["createdAt"]!.GetValue<DateTimeOffset>())).ToList(), page.Total);
    }

    public Task SignOutEverywhereAsync(VisitorRef input, CancellationToken ct) =>
        Visitors.RunActionAsync("sign-out", [input.VisitorId.ToString()], null, ct);

    public Task MarkEmailVerifiedAsync(VisitorRef input, CancellationToken ct) =>
        Visitors.RunActionAsync("verify", [input.VisitorId.ToString()], null, ct);

    public async Task DeleteAsync(VisitorRef input, CancellationToken ct)
    {
        if (!await Visitors.DeleteAsync(input.VisitorId.ToString(), ct))
        {
            throw new ContractValidationException($"No visitor {input.VisitorId}.");
        }
    }
}
