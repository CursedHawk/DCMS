using Dcms.Plugins.DynamicApps.Metadata;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Data.DynamicApps;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.DynamicApps.Automation;

/// <summary>
/// One app's side of retiring flows (ADR 0029, F11): its published flows written as functions,
/// and then — once they are live as functions — the switch that stops every flow of it from
/// starting again. Runs in the app's plugin context; the platform's migration drives it.
/// </summary>
public sealed class FlowExport(AppsDbContext db, ConfigurationService configuration, IPluginContext context)
{
    /// <param name="Vouched">What the published revision's publisher was checked for: the provided actions' permissions.</param>
    public sealed record Export(int Revision, IReadOnlyList<string> Vouched, FlowConverter.Result Code);

    private Guid InstanceId => context.Instance?.InstanceId ?? throw new InvalidOperationException("An app's flows are per instance.");

    /// <summary>The published flows as functions; null when there are none, or they are retired already.</summary>
    /// <exception cref="FlowConversionException">A flow uses something functions have no equivalent of.</exception>
    public async Task<Export?> ConvertAsync(FlowConverter.Providers providers, ISet<string> taken, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.InstanceId == InstanceId, ct);
        if (app is null || app.FlowsRetiredAt is not null || await configuration.GetPublishedAsync(ct) is not { } published
            || published.Config.Flows.Count == 0)
        {
            return null;
        }
        var vouched = await db.Revisions.AsNoTracking().Where(r => r.Id == app.PublishedRevisionId)
            .Select(r => r.VouchedPermissions).FirstOrDefaultAsync(ct);
        return new Export(published.Revision.Number, vouched ?? [],
            FlowConverter.Convert(context.Instance!.Slug, published.Config, providers, taken));
    }

    /// <summary>Stops (or, undoing a migration that failed after it, restarts) every flow of this app.</summary>
    public async Task RetireAsync(bool retired, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var app = await db.Apps.FirstOrDefaultAsync(a => a.InstanceId == InstanceId, ct);
        if (app is null) return;
        app.FlowsRetiredAt = retired ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync(ct);
    }
}
