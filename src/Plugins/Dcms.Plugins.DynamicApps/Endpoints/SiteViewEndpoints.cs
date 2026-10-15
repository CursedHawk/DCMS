using System.Text.Json;
using Dcms.Plugins.DynamicApps.Data;
using Dcms.Plugins.UserAuth.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Data.DynamicApps;
using Dcms.Shared.Kernel.Views;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.DynamicApps.Endpoints;

/// <summary>
/// Saved views of an app's tables on the tenant's sites (#297), <c>/api/{slug}/views/{table}</c>:
/// the app's own views for anyone who may read the table, and for a signed-in User Authentication
/// user their own views and those shared with them — with everyone signed in, with one of their
/// groups, or with them. Only the owner changes or shares a view. Every call first reads the table
/// as the records' own routes would, so a view never shows a table its holder could not open, and
/// its records are judged again on every query.
/// </summary>
internal static class SiteViewEndpoints
{
    public const int MaxViewsPerTable = 50;
    public const int MaxSharedGroups = 50;
    public const int MaxSharedPeople = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Gate = "A site user's own views of a table they may read, checked per call; changes need a signed-in User Authentication user.";

    private static readonly string[] SystemFields = ["id", "created_at", "updated_at"];

    public static void Map(IPluginEndpointBuilder endpoints)
    {
        // Whom a view can be shared with: the site's people and groups, for a signed-in user only.
        endpoints.MapGet("/views/_people", (string? search, string? ids, IPluginContext context, CancellationToken ct) =>
                SignedIn(context, ct, async (_, people) =>
                {
                    var wanted = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToList();
                    if (wanted.Count > MaxSharedPeople || search?.Length > 100)
                    {
                        return Results.BadRequest(new { error = $"Ask for at most {MaxSharedPeople} people, or search with up to 100 characters." });
                    }
                    return Results.Ok(await people.FindAsync(new PeopleSearch(search, wanted.Count > 0 ? wanted : null), ct));
                }))
            .WithoutPermission(Gate);

        endpoints.MapGet("/views/_groups", (IPluginContext context, CancellationToken ct) =>
                SignedIn(context, ct, async (_, people) => Results.Ok(await people.ListGroupsAsync(ct))))
            .WithoutPermission(Gate);

        // The views this visitor may use on a table, and the one it opens with.
        endpoints.MapGet("/views/{table}", (string table, RecordService records, AppsDbContext db, IPluginContext context, CancellationToken ct) =>
                PublicEndpoints.Run(records, context, ct, async () =>
                {
                    var (model, runtime) = await records.ReadableTableAsync(table, ct);
                    var system = model.Config.Views.Where(v => v.TableId == runtime.Id).Select(v => new
                    {
                        id = $"system:{v.Id}",
                        name = v.DisplayName,
                        system = true,
                        mine = false,
                        definition = new ViewDefinition
                        {
                            Columns = v.Columns.Select(id => runtime.ById.GetValueOrDefault(id)).Where(m => m is not null && RecordCodec.Visible(m, RecordPlane.Public))
                                .Select(m => m!.ApiName).ToList(),
                            Sort = v.Sort.Select(s => runtime.ById.GetValueOrDefault(s.FieldId)).Zip(v.Sort)
                                .Where(x => x.First is not null && RecordCodec.Visible(x.First, RecordPlane.Public))
                                .Select(x => new ViewSort(x.First!.ApiName, x.Second.Descending ? "desc" : "asc")).ToList(),
                        },
                    }).ToList<object>();
                    var makerDefault = model.Config.Views.FirstOrDefault(v => v.TableId == runtime.Id && v.IsDefault) is { } d ? $"system:{d.Id}" : null;

                    if (await UserAsync(context, ct) is not { } user)
                    {
                        return Results.Ok(new { signedIn = false, views = system, defaultView = makerDefault });
                    }
                    var instanceId = context.Instance!.InstanceId;
                    var groups = user.Groups.ToList();
                    var mineOrShared = await db.SiteViews.AsNoTracking()
                        .Where(v => v.InstanceId == instanceId && v.TableId == runtime.Id
                            && (v.OwnerUserId == user.Id || v.SharedWithEveryone || v.SharedUsers.Contains(user.Id) || v.SharedGroups.Any(g => groups.Contains(g))))
                        .OrderBy(v => v.Name)
                        .ToListAsync(ct);
                    var chosen = await db.SiteViewDefaults.AsNoTracking()
                        .Where(x => x.InstanceId == instanceId && x.TableId == runtime.Id && x.UserId == user.Id)
                        .Select(x => x.ViewKey).FirstOrDefaultAsync(ct);
                    return Results.Ok(new
                    {
                        signedIn = true,
                        views = system.Concat(mineOrShared.Select(v => Shape(v, user.Id))),
                        defaultView = chosen ?? makerDefault,
                    });
                }))
            .WithoutPermission(Gate);

        endpoints.MapPost("/views/{table}", (string table, SaveSiteViewRequest body, RecordService records, AppsDbContext db, IPluginContext context, CancellationToken ct) =>
                Owner(table, records, context, ct, async (model, runtime, user) =>
                {
                    if (Invalid(body, runtime) is { } bad)
                    {
                        return bad;
                    }
                    var instanceId = context.Instance!.InstanceId;
                    if (await db.SiteViews.CountAsync(v => v.InstanceId == instanceId && v.TableId == runtime.Id && v.OwnerUserId == user.Id, ct) >= MaxViewsPerTable)
                    {
                        return Results.Conflict(new { error = $"You can keep at most {MaxViewsPerTable} views of one table." });
                    }
                    var view = new AppSiteView
                    {
                        Id = Guid.NewGuid(),
                        InstanceId = instanceId,
                        TableId = runtime.Id,
                        Name = body.Name!.Trim(),
                        OwnerUserId = user.Id,
                        OwnerName = OwnerName(user),
                        DefinitionJson = JsonSerializer.Serialize(body.Definition, Json),
                    };
                    db.SiteViews.Add(view);
                    await db.SaveChangesAsync(ct);
                    return Results.Created($"views/{table}/{view.Id}", Shape(view, user.Id));
                }))
            .WithoutPermission(Gate)
            .AuditAs("view.created");

        endpoints.MapPut("/views/{table}/{id:guid}", (string table, Guid id, SaveSiteViewRequest body, RecordService records, AppsDbContext db, IPluginContext context,
                CancellationToken ct) =>
                Owner(table, records, context, ct, async (model, runtime, user) =>
                {
                    if (Invalid(body, runtime) is { } bad)
                    {
                        return bad;
                    }
                    if (await OwnedAsync(db, context, runtime, user, id, ct) is not { } view)
                    {
                        return NotYours();
                    }
                    view.Name = body.Name!.Trim();
                    view.OwnerName = OwnerName(user);
                    view.DefinitionJson = JsonSerializer.Serialize(body.Definition, Json);
                    view.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return Results.Ok(Shape(view, user.Id));
                }))
            .WithoutPermission(Gate)
            .AuditAs("view.updated");

        endpoints.MapDelete("/views/{table}/{id:guid}", (string table, Guid id, RecordService records, AppsDbContext db, IPluginContext context, CancellationToken ct) =>
                Owner(table, records, context, ct, async (model, runtime, user) =>
                {
                    if (await OwnedAsync(db, context, runtime, user, id, ct) is not { } view)
                    {
                        return NotYours();
                    }
                    var key = view.Id.ToString();
                    var instanceId = context.Instance!.InstanceId;
                    // Whoever opened the table with it opens it as if they had chosen nothing.
                    db.SiteViewDefaults.RemoveRange(await db.SiteViewDefaults.Where(d => d.InstanceId == instanceId && d.TableId == runtime.Id && d.ViewKey == key).ToListAsync(ct));
                    db.SiteViews.Remove(view);
                    await db.SaveChangesAsync(ct);
                    return Results.NoContent();
                }))
            .WithoutPermission(Gate)
            .AuditAs("view.deleted");

        // Who else may use it: everyone signed in to the tenant's sites, groups, or people, all checked
        // against the site's own directory. Read-only for them; the owner alone changes it.
        endpoints.MapPut("/views/{table}/{id:guid}/sharing", (string table, Guid id, SiteViewSharing body, RecordService records, AppsDbContext db,
                IPluginContext context, CancellationToken ct) =>
                Owner(table, records, context, ct, async (model, runtime, user) =>
                {
                    var groups = (body.Groups ?? []).Distinct().ToList();
                    var people = (body.Users ?? []).Where(u => u != user.Id).Distinct().ToList();
                    if (groups.Count > MaxSharedGroups || people.Count > MaxSharedPeople)
                    {
                        return Results.BadRequest(new { error = $"Share with at most {MaxSharedGroups} groups and {MaxSharedPeople} people." });
                    }
                    if (await OwnedAsync(db, context, runtime, user, id, ct) is not { } view)
                    {
                        return NotYours();
                    }
                    var directory = context.Contracts.TryGet<IUserPeople>();
                    if ((groups.Count > 0 || people.Count > 0) && directory is null)
                    {
                        return Results.BadRequest(new { error = "This site has no directory to share with groups or people." });
                    }
                    if (groups.Count > 0)
                    {
                        var known = (await directory!.ListGroupsAsync(ct)).Groups.Select(g => g.Id).ToHashSet();
                        if (groups.Any(g => !known.Contains(g)))
                        {
                            return Results.BadRequest(new { error = "A group shared with is not one of this site's." });
                        }
                    }
                    if (people.Count > 0)
                    {
                        var known = (await directory!.FindAsync(new PeopleSearch(Ids: people), ct)).Items.Select(p => p.Id).ToHashSet();
                        if (people.Any(p => !known.Contains(p)))
                        {
                            return Results.BadRequest(new { error = "Someone shared with is not a user of this site." });
                        }
                    }
                    view.SharedWithEveryone = body.Everyone;
                    view.SharedGroups = groups;
                    view.SharedUsers = people;
                    view.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return Results.Ok(Shape(view, user.Id));
                }))
            .WithoutPermission(Gate)
            .AuditAs("view.shared");

        // The view this user opens the table with: one they can see, one of the app's, or none.
        endpoints.MapPut("/views/{table}/default", (string table, SiteViewDefaultRequest body, RecordService records, AppsDbContext db, IPluginContext context,
                CancellationToken ct) =>
                Owner(table, records, context, ct, async (model, runtime, user) =>
                {
                    var instanceId = context.Instance!.InstanceId;
                    var key = body.ViewKey;
                    if (key is not null)
                    {
                        var groups = user.Groups.ToList();
                        var usable = key.StartsWith("system:", StringComparison.Ordinal)
                            ? Guid.TryParse(key["system:".Length..], out var systemId) && model.Config.Views.Any(v => v.Id == systemId && v.TableId == runtime.Id)
                            : Guid.TryParse(key, out var viewId) && await db.SiteViews.AnyAsync(v => v.Id == viewId && v.InstanceId == instanceId && v.TableId == runtime.Id
                                && (v.OwnerUserId == user.Id || v.SharedWithEveryone || v.SharedUsers.Contains(user.Id) || v.SharedGroups.Any(g => groups.Contains(g))), ct);
                        if (!usable)
                        {
                            return Results.BadRequest(new { error = "That is not a view of this table you can use." });
                        }
                    }
                    var existing = await db.SiteViewDefaults.FirstOrDefaultAsync(d => d.InstanceId == instanceId && d.TableId == runtime.Id && d.UserId == user.Id, ct);
                    if (key is null && existing is not null)
                    {
                        db.SiteViewDefaults.Remove(existing);
                    }
                    else if (key is not null && existing is not null)
                    {
                        existing.ViewKey = key;
                    }
                    else if (key is not null)
                    {
                        db.SiteViewDefaults.Add(new AppSiteViewDefault { Id = Guid.NewGuid(), InstanceId = instanceId, TableId = runtime.Id, UserId = user.Id, ViewKey = key });
                    }
                    await db.SaveChangesAsync(ct);
                    return Results.NoContent();
                }))
            .WithoutPermission(Gate)
            .AuditAs("view.default-set");
    }

    private static object Shape(AppSiteView view, Guid me) => new
    {
        id = view.Id.ToString(),
        name = view.Name,
        system = false,
        mine = view.OwnerUserId == me,
        owner = view.OwnerUserId == me ? null : view.OwnerName,
        definition = JsonSerializer.Deserialize<ViewDefinition>(view.DefinitionJson, Json),
        // Whom it is shared with is the owner's business only.
        sharing = view.OwnerUserId == me
            ? new { everyone = view.SharedWithEveryone, groups = view.SharedGroups, users = view.SharedUsers }
            : null,
        updatedAt = view.UpdatedAt,
    };

    private static async Task<SiteUser?> UserAsync(IPluginContext context, CancellationToken ct) =>
        context.Contracts.TryGet<IUserIdentity>() is { } identity ? (await identity.GetCurrentAsync(ct)).User : null;

    private static string OwnerName(SiteUser user)
    {
        var name = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email : user.DisplayName.Trim();
        return name.Length > 200 ? name[..200] : name;
    }

    /// <summary>A change to a view of a readable table, by a signed-in User Authentication user.</summary>
    private static Task<IResult> Owner(string table, RecordService records, IPluginContext context, CancellationToken ct,
        Func<RuntimeModel, RuntimeTable, SiteUser, Task<IResult>> handler) =>
        PublicEndpoints.Run(records, context, ct, async () =>
        {
            var (model, runtime) = await records.ReadableTableAsync(table, ct);
            return await UserAsync(context, ct) is { } user
                ? await handler(model, runtime, user)
                : Results.Json(new { error = "Sign in to save and share views." }, statusCode: StatusCodes.Status401Unauthorized);
        });

    private static async Task<IResult> SignedIn(IPluginContext context, CancellationToken ct, Func<SiteUser, IUserPeople, Task<IResult>> handler)
    {
        if (await UserAsync(context, ct) is not { } user)
        {
            return Results.Json(new { error = "Sign in to share views." }, statusCode: StatusCodes.Status401Unauthorized);
        }
        return context.Contracts.TryGet<IUserPeople>() is { } people
            ? await handler(user, people)
            : Results.NotFound(new { error = "This site has no directory to share with." });
    }

    private static Task<AppSiteView?> OwnedAsync(AppsDbContext db, IPluginContext context, RuntimeTable table, SiteUser user, Guid id, CancellationToken ct)
    {
        var instanceId = context.Instance!.InstanceId;
        return db.SiteViews.FirstOrDefaultAsync(v => v.Id == id && v.InstanceId == instanceId && v.TableId == table.Id && v.OwnerUserId == user.Id, ct);
    }

    // The same answer for a view that is not there and one that is someone else's.
    private static IResult NotYours() => Results.NotFound(new { error = "You have no such view of this table." });

    /// <summary>A name, and a definition naming only fields the site can see of this table.</summary>
    private static IResult? Invalid(SaveSiteViewRequest body, RuntimeTable table)
    {
        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Trim().Length > 100)
        {
            return Results.BadRequest(new { error = "A view needs a name of up to 100 characters." });
        }
        if (body.Definition is not { } definition)
        {
            return Results.BadRequest(new { error = "A view needs a definition." });
        }
        try
        {
            definition.Validate();
        }
        catch (ViewDefinitionException e)
        {
            return Results.BadRequest(new { error = e.Message });
        }
        bool Known(string field) => SystemFields.Contains(field)
            || (table.ByName.TryGetValue(field, out var member) && RecordCodec.Visible(member, RecordPlane.Public));
        var unknown = definition.Columns.Concat(definition.Sort.Select(s => s.Field)).Concat(FilterFields(definition.Filter)).FirstOrDefault(f => !Known(f));
        return unknown is null ? null : Results.BadRequest(new { error = $"{table.ApiName} has no field '{unknown}'." });
    }

    private static IEnumerable<string> FilterFields(ViewFilter? filter) => filter switch
    {
        null => [],
        { And: { } and } => and.SelectMany(FilterFields),
        { Or: { } or } => or.SelectMany(FilterFields),
        { Not: { } not } => FilterFields(not),
        _ => [filter.Field ?? ""],
    };
}

internal sealed record SaveSiteViewRequest(string? Name, ViewDefinition? Definition);

internal sealed record SiteViewSharing(bool Everyone, IReadOnlyList<Guid>? Groups, IReadOnlyList<Guid>? Users);

/// <param name="ViewKey">A view id, <c>system:{id}</c> for one of the app's own, or null for none.</param>
internal sealed record SiteViewDefaultRequest(string? ViewKey);
