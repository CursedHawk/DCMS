using System.Text.Json;
using System.Text.RegularExpressions;
using Dcms.AdminApi.Tenancy;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Kernel.Views;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Views;

/// <summary>
/// Saved views of console tables (#278): a member's own, those shared with them (directly, by
/// role, or with the whole workspace), and which one each table opens with. Only the owner
/// changes or shares a view. Every call is about one table, and needs read on it: a view never
/// shows a table its holder could not open.
/// </summary>
public static partial class TableViewEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapTableViewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/views")
            .RequireAuthorization()
            .PermissionExempt("Each view belongs to a table; read on that table is checked per request.");

        // The views this member may use on a table, and the one it opens with.
        group.MapGet("", async (string table, ViewContext ctx, CancellationToken ct) =>
        {
            if (await ctx.DenyAsync(table, ct) is { } denied) return denied;
            var me = ctx.UserId;
            var roles = await ctx.MyRolesAsync(ct);
            var tenantId = ctx.TenantId;
            var views = await ctx.Db.TableViews.AsNoTracking().Include(v => v.Shares)
                .Where(v => v.TenantId == tenantId && v.TableKey == table && (v.OwnerUserId == me || v.SharedWithWorkspace
                    || v.Shares.Any(s => s.UserId == me || (s.RoleId != null && roles.Contains(s.RoleId.Value)))))
                .OrderBy(v => v.Name)
                .ToListAsync(ct);
            var owners = views.Select(v => v.OwnerUserId).Distinct().ToList();
            var emails = await ctx.Db.Memberships.AsNoTracking().Where(m => m.TenantId == tenantId && owners.Contains(m.UserId))
                .ToDictionaryAsync(m => m.UserId, m => m.Email, ct);
            var fallback = await ctx.Db.TableViewDefaults.AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.UserId == me && d.TableKey == table).Select(d => d.ViewKey).FirstOrDefaultAsync(ct);

            return Results.Ok(new
            {
                views = views.Select(v => Shape(v, me, emails.GetValueOrDefault(v.OwnerUserId))),
                defaultView = fallback,
            });
        });

        group.MapPost("", async (SaveViewRequest body, ViewContext ctx, AuditScope audit, CancellationToken ct) =>
        {
            if (await ctx.DenyAsync(body.Table, ct) is { } denied) return denied;
            if (Invalid(body) is { } bad) return bad;
            var view = new TableView
            {
                Id = Guid.NewGuid(),
                TenantId = ctx.TenantId,
                TableKey = body.Table!,
                Name = body.Name!.Trim(),
                OwnerUserId = ctx.UserId,
                DefinitionJson = JsonSerializer.Serialize(body.Definition, Json),
            };
            ctx.Db.TableViews.Add(view);
            audit.Declared?.For("view", view.Id, view.Name).With("table", view.TableKey);
            await ctx.Db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/views/{view.Id}", Shape(view, ctx.UserId, null));
        }).WithAudit(AuditActions.TableViewCreated, "view");

        group.MapPut("/{id:guid}", async (Guid id, SaveViewRequest body, ViewContext ctx, AuditScope audit, CancellationToken ct) =>
        {
            var view = await ctx.Db.TableViews.Include(v => v.Shares).FirstOrDefaultAsync(v => v.Id == id, ct);
            if (await ctx.OwnedAsync(view, ct) is { } refused) return refused;
            if (Invalid(body with { Table = view!.TableKey }) is { } bad) return bad;
            view.Name = body.Name!.Trim();
            view.DefinitionJson = JsonSerializer.Serialize(body.Definition, Json);
            view.UpdatedAt = DateTimeOffset.UtcNow;
            audit.Declared?.For("view", view.Id, view.Name).With("table", view.TableKey);
            await ctx.Db.SaveChangesAsync(ct);
            return Results.Ok(Shape(view, ctx.UserId, null));
        }).WithAudit(AuditActions.TableViewUpdated, "view");

        group.MapDelete("/{id:guid}", async (Guid id, ViewContext ctx, AuditScope audit, CancellationToken ct) =>
        {
            var view = await ctx.Db.TableViews.FirstOrDefaultAsync(v => v.Id == id, ct);
            if (await ctx.OwnedAsync(view, ct) is { } refused) return refused;
            // Nobody opens a table with a view that is gone.
            var key = view!.Id.ToString();
            await ctx.Db.TableViewDefaults.Where(d => d.TenantId == view.TenantId && d.ViewKey == key).ExecuteDeleteAsync(ct);
            ctx.Db.TableViews.Remove(view);
            audit.Declared?.For("view", view.Id, view.Name).With("table", view.TableKey);
            await ctx.Db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithAudit(AuditActions.TableViewDeleted, "view");

        // Who else sees it: everyone in the workspace, or the members and roles named. Replaces
        // the whole list; shared views are read-only to everyone but the owner.
        group.MapPut("/{id:guid}/sharing", async (Guid id, ShareViewRequest body, ViewContext ctx, AuditScope audit, CancellationToken ct) =>
        {
            var view = await ctx.Db.TableViews.FirstOrDefaultAsync(v => v.Id == id, ct);
            if (await ctx.OwnedAsync(view, ct) is { } refused) return refused;
            var userIds = (body.UserIds ?? []).Where(u => u != ctx.UserId).Distinct().ToList();
            var roleIds = (body.RoleIds ?? []).Distinct().ToList();
            if (userIds.Count + roleIds.Count > 100)
                return Results.BadRequest(new { error = "Share with at most 100 members and roles; share with the workspace instead." });
            // Only this workspace's members and roles: a view never leaves the workspace it was made in.
            var tenantId = ctx.TenantId;
            if (await ctx.Db.Memberships.CountAsync(m => m.TenantId == tenantId && userIds.Contains(m.UserId), ct) != userIds.Count)
                return Results.BadRequest(new { error = "Someone named is not a member of this workspace." });
            if (await ctx.Db.TenantRoles.CountAsync(r => r.TenantId == tenantId && roleIds.Contains(r.Id), ct) != roleIds.Count)
                return Results.BadRequest(new { error = "A role named does not exist in this workspace." });

            // The old set goes with ExecuteDelete, the new one is added as new rows (see the EF trap below).
            await using var tx = await ctx.Db.Database.BeginTransactionAsync(ct);
            await ctx.Db.TableViewShares.Where(x => x.TenantId == tenantId && x.ViewId == view!.Id).ExecuteDeleteAsync(ct);
            view!.SharedWithWorkspace = body.Workspace;
            view.UpdatedAt = DateTimeOffset.UtcNow;
            // Added through the set, not the navigation: a child with its id already set, found
            // through a navigation, is taken for an existing row and UPDATEd.
            List<TableViewShare> shares = [
                .. userIds.Select(u => new TableViewShare { Id = Guid.NewGuid(), TenantId = view.TenantId, ViewId = view.Id, UserId = u }),
                .. roleIds.Select(r => new TableViewShare { Id = Guid.NewGuid(), TenantId = view.TenantId, ViewId = view.Id, RoleId = r }),
            ];
            ctx.Db.TableViewShares.AddRange(shares);
            audit.Declared?.For("view", view.Id, view.Name)
                .With("workspace", body.Workspace).With("members", userIds.Count).With("roles", roleIds.Count);
            await ctx.Db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(Shape(view, ctx.UserId, null));
        }).WithAudit(AuditActions.TableViewShared, "view");

        // Who a view can be shared with: the workspace's members and roles. Listing members takes
        // members:manage elsewhere; sharing a view is open to every member, so it gets this —
        // names and emails only, and only to a member of the workspace.
        group.MapGet("/audience", async (ViewContext ctx, CurrentUser user, CancellationToken ct) =>
        {
            if (ctx.NoWorkspace() is { } none) return none;
            var me = ctx.UserId;
            var tenantId = ctx.TenantId;
            if (!user.IsSuperAdmin && !await ctx.Db.Memberships.AnyAsync(m => m.TenantId == tenantId && m.UserId == me, ct)) return Results.Forbid();
            var people = await ctx.Db.Memberships.AsNoTracking().Where(m => m.TenantId == tenantId && m.UserId != me)
                .OrderBy(m => m.Email).Select(m => new { id = m.UserId, label = m.Email }).ToListAsync(ct);
            var roles = await ctx.Db.TenantRoles.AsNoTracking().Where(r => r.TenantId == tenantId)
                .OrderBy(r => r.Name).Select(r => new { id = r.Id, label = r.Name }).ToListAsync(ct);
            return Results.Ok(new { people, roles });
        });

        // The view this member opens the table with: a view they can see, a table's own built-in
        // view (system:…), or null for the table's default.
        group.MapPut("/default", async (DefaultViewRequest body, ViewContext ctx, CancellationToken ct) =>
        {
            if (await ctx.DenyAsync(body.Table, ct) is { } denied) return denied;
            var me = ctx.UserId;
            if (body.View is { } key && !key.StartsWith("system:", StringComparison.Ordinal))
            {
                var roles = await ctx.MyRolesAsync(ct);
                var tenantOfView = ctx.TenantId;
                var visible = Guid.TryParse(key, out var viewId) && await ctx.Db.TableViews.AnyAsync(v => v.Id == viewId
                    && v.TenantId == tenantOfView && v.TableKey == body.Table && (v.OwnerUserId == me || v.SharedWithWorkspace
                        || v.Shares.Any(s => s.UserId == me || (s.RoleId != null && roles.Contains(s.RoleId.Value)))), ct);
                if (!visible) return Results.BadRequest(new { error = "No such view on this table." });
            }
            if (body.View?.Length > 100) return Results.BadRequest(new { error = "No such view on this table." });

            var tenantId = ctx.TenantId;
            var row = await ctx.Db.TableViewDefaults.FirstOrDefaultAsync(d => d.TenantId == tenantId && d.UserId == me && d.TableKey == body.Table, ct);
            if (body.View is null)
            {
                if (row is not null) ctx.Db.TableViewDefaults.Remove(row);
            }
            else if (row is null)
            {
                ctx.Db.TableViewDefaults.Add(new TableViewDefault { Id = Guid.NewGuid(), TenantId = tenantId, UserId = me, TableKey = body.Table!, ViewKey = body.View });
            }
            else
            {
                row.ViewKey = body.View;
            }
            await ctx.Db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).AuditExempt("A member's own preference for which view opens first.");

        return app;
    }

    private static object Shape(TableView v, Guid me, string? ownerEmail)
    {
        var mine = v.OwnerUserId == me;
        return new
        {
            id = v.Id,
            table = v.TableKey,
            name = v.Name,
            mine,
            owner = mine ? null : ownerEmail,
            definition = JsonSerializer.Deserialize<ViewDefinition>(v.DefinitionJson, Json),
            sharedWithWorkspace = v.SharedWithWorkspace,
            // Who it is shared with is the owner's business.
            sharedWith = mine ? new
            {
                userIds = v.Shares.Where(s => s.UserId != null).Select(s => s.UserId!.Value),
                roleIds = v.Shares.Where(s => s.RoleId != null).Select(s => s.RoleId!.Value),
            } : null,
            updatedAt = v.UpdatedAt,
        };
    }

    private static IResult? Invalid(SaveViewRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Trim().Length > 128)
            return Results.BadRequest(new { error = "A view needs a name of at most 128 characters." });
        if (body.Definition is null) return Results.BadRequest(new { error = "A view needs a definition." });
        try
        {
            body.Definition.Validate();
        }
        catch (ViewDefinitionException e)
        {
            return Results.BadRequest(new { error = e.Message });
        }
        return null;
    }

    /// <summary>A table key: <c>content:{instance}:{type}</c>, <c>apps:{instance}:{table}</c> or <c>dataset:{instance}:{set}</c>.</summary>
    [GeneratedRegex("^(content|apps|dataset):[a-z0-9-]{1,100}:[A-Za-z0-9_.-]{1,100}$")]
    internal static partial Regex TableKeyPattern();

    private sealed record SaveViewRequest(string? Table, string? Name, ViewDefinition? Definition);
    private sealed record ShareViewRequest(bool Workspace, IReadOnlyList<Guid>? UserIds, IReadOnlyList<Guid>? RoleIds);
    private sealed record DefaultViewRequest(string? Table, string? View);
}

/// <summary>Who is asking, and whether they may read a table, for <see cref="TableViewEndpoints"/>.</summary>
public sealed class ViewContext(
    TenancyDbContext db, CmsDbContext cms, CurrentUser user, IPluginCatalog catalog,
    IAuthorizationService authz, IHttpContextAccessor http, ITenantContext tenant)
{
    public TenancyDbContext Db => db;
    public Guid UserId => user.RequireUserId();

    /// <summary>
    /// The workspace the request is in. A view lives in one workspace and is seen, shared and
    /// opened only there: every read and write names it, not only the context's query filter.
    /// </summary>
    public Guid TenantId => tenant.TenantId ?? throw new InvalidOperationException("No workspace.");

    /// <summary>Null when the request names a workspace; else the answer to give.</summary>
    public IResult? NoWorkspace() =>
        tenant.TenantId is null ? Results.BadRequest(new { error = "Select a workspace first." }) : null;

    public async Task<HashSet<Guid>> MyRolesAsync(CancellationToken ct)
    {
        var me = UserId;
        var tenantId = TenantId;
        return (await db.MemberRoles.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Membership!.UserId == me).Select(r => r.TenantRoleId).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>Null when the caller may read the table; else the answer to give.</summary>
    public async Task<IResult?> DenyAsync(string? table, CancellationToken ct)
    {
        if (NoWorkspace() is { } none) return none;
        if (table is null || !TableViewEndpoints.TableKeyPattern().IsMatch(table))
            return Results.BadRequest(new { error = "Name the table: content:{instance}:{type}, apps:{instance}:{table} or dataset:{instance}:{set}." });
        var permission = await ReadPermissionAsync(table, ct);
        if (permission is null) return Results.NotFound();
        var allowed = await authz.AuthorizeAsync(http.HttpContext!.User, null, PermissionPolicyProvider.PolicyName(permission));
        return allowed.Succeeded ? null : Results.Forbid();
    }

    /// <summary>Not found for a view that is not there or not the caller's to change, so ids are not probed.</summary>
    public async Task<IResult?> OwnedAsync(TableView? view, CancellationToken ct)
    {
        if (NoWorkspace() is { } none) return none;
        if (view is null || view.TenantId != TenantId || view.OwnerUserId != UserId) return Results.NotFound();
        return await DenyAsync(view.TableKey, ct);
    }

    /// <summary>The permission reading the table takes; null when there is no such table.</summary>
    private async Task<string?> ReadPermissionAsync(string table, CancellationToken ct)
    {
        var parts = table.Split(':');
        if (parts[0] == "content") return PlatformPermissions.ContentRead;

        var instance = await cms.PluginInstances.AsNoTracking()
            .Where(p => p.Slug == parts[1]).Select(p => p.PluginId).FirstOrDefaultAsync(ct);
        if (instance is null) return null;
        if (parts[0] == "apps")
        {
            return instance == "dynamic-apps" ? PluginPermissions.Resolve(instance, "data-read") : null;
        }
        var set = catalog.Find(instance)?.DataSets?.FirstOrDefault(d => d.Id == parts[2]);
        return PluginPermissions.Resolve(instance, set?.ReadPermission ?? PlatformPermissions.PluginsManage);
    }
}
