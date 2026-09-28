using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Visitors;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.VisitorAuth.Contracts;

/// <summary>
/// Reads the visitor token of the current request. Outside a request (a job, an event handler)
/// there is nobody signed in, which is the correct answer rather than an error.
/// </summary>
public sealed class VisitorIdentity(IPluginContext context, IHttpContextAccessor http, VisitorsDbContext db) : IVisitorIdentity
{
    public async Task<CurrentVisitor> GetCurrentAsync(CancellationToken ct)
    {
        if (http.HttpContext is not { } request || await request.TryAuthenticateVisitorAsync() is not { } visitorId)
        {
            return new CurrentVisitor(null);
        }
        using var rls = RlsScope.Tenant(context.TenantId);
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == visitorId, ct);
        return new CurrentVisitor(account is null ? null : VisitorProfiles.ToProfile(account, context));
    }
}

public sealed class VisitorProfiles(IPluginContext context, VisitorsDbContext db, ILogger<VisitorProfiles> logger) : IVisitorProfiles
{
    public async Task<VisitorProfile?> GetAsync(VisitorRef input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == input.VisitorId, ct);
        return account is null ? null : ToProfile(account, context);
    }

    public async Task<PublicVisitorProfile?> GetPublicAsync(VisitorRef input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == input.VisitorId, ct);
        if (account is null)
        {
            return null;
        }
        var values = VisitorAttributes.Parse(account.AttributesJson);
        return new PublicVisitorProfile(
            account.Id, account.DisplayName,
            VisitorAttributes.Visible(values, Definitions(context), AttributeVisibility.Public));
    }

    public Task<AttributeDefinitionList> ListAttributeDefinitionsAsync(CancellationToken ct) =>
        Task.FromResult(new AttributeDefinitionList(Definitions(context)));

    public async Task<VisitorProfile> SetAttributesAsync(SetVisitorAttributes input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == input.VisitorId, ct)
            ?? throw new ContractValidationException("No such visitor.");
        var definitions = Definitions(context);

        // Another plugin (or an admin through the contract) may only touch what it can see.
        // Private attributes are the visitor's own and change on their profile page or in the
        // VisitorAuth admin, never through the contract.
        var next = VisitorAttributes.Apply(
            VisitorAttributes.Parse(account.AttributesJson), input.Attributes, definitions,
            d => d.Visibility >= AttributeVisibility.Plugins, out var changed);
        if (changed.Count > 0)
        {
            account.AttributesJson = VisitorAttributes.Serialize(next);
            await db.SaveChangesAsync(ct);
            await PublishUpdatedAsync(context, account.Id, changed, logger, ct);
        }
        return ToProfile(account, context);
    }

    internal static IReadOnlyList<AttributeDefinition> Definitions(IPluginContext context) =>
        context.Instance is { } instance ? VisitorAttributes.Read(instance.Config) : [];

    internal static VisitorProfile ToProfile(VisitorAccount account, IPluginContext context) =>
        new(account.Id, account.Email, account.DisplayName,
            VisitorAttributes.Visible(VisitorAttributes.Parse(account.AttributesJson), Definitions(context), AttributeVisibility.Plugins),
            account.CreatedAt);

    /// <summary>Best effort: the change is saved; a bus hiccup must not turn it into an error.</summary>
    internal static async Task PublishUpdatedAsync(
        IPluginContext context, Guid visitorId, IReadOnlyList<string> changed, ILogger logger, CancellationToken ct)
    {
        try
        {
            await context.PublishAsync(new VisitorProfileUpdated(visitorId, changed), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not publish visitor.profile.updated for {VisitorId}.", visitorId);
        }
    }
}
