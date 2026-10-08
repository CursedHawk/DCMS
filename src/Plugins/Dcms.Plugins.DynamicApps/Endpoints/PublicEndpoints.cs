using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Automation;
using Dcms.Plugins.DynamicApps.Data;
using Dcms.Plugins.UserAuth.Api;
using Dcms.Plugins.VisitorAuth.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Dcms.Plugins.DynamicApps.Endpoints;

/// <summary>
/// The application's public API, <c>/api/{slug}/data/{table}…</c> in content-api — under
/// <c>data/</c> because content delivery owns <c>/api/{slug}/{contentType}</c> for every plugin
/// instance — plus <c>/api/{slug}/_model</c>: the published tables the
/// site may read or write, as each table's public access allows. Every route reaches
/// <see cref="RecordService"/> on the public plane, which decides what this visitor may see and
/// do; the routes only say who the visitor is. Unpublished configuration never reaches here.
/// </summary>
internal static class PublicEndpoints
{
    private const string Public = "The app's public API: what it allows is each table's public access, enforced per call.";

    private const int MaxFlowInput = 16 * 1024;

    public static void Map(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapGet("/_model", async (RuntimeModelProvider models, CancellationToken ct) =>
                await models.GetAsync(ct) is { } model ? Results.Ok(PublicApi.Model(model)) : Results.NotFound(new { error = "Nothing is published yet." }))
            .WithoutPermission(Public);

        endpoints.MapGet("/data/{table}", (string table, int? page, int? pageSize, string? sort, string? search, string? select, string? expand,
                RecordService records, IPluginContext context, CancellationToken ct) =>
                Run(records, context, ct, async () => Results.Ok(await records.QueryAsync(table, new RecordQuery
                {
                    Page = page ?? 1,
                    PageSize = pageSize ?? 50,
                    Search = search,
                    Select = List(select),
                    Expand = List(expand),
                    Sort = List(sort).Select(s => s.StartsWith('-') ? new RecordSort(s[1..], SortDirection.Desc) : new RecordSort(s)).ToList(),
                }, RecordPlane.Public, ct))))
            .WithoutPermission(Public);

        endpoints.MapPost("/data/{table}/query", (string table, RecordQuery query, RecordService records, IPluginContext context, CancellationToken ct) =>
                Run(records, context, ct, async () => Results.Ok(await records.QueryAsync(table, query, RecordPlane.Public, ct))))
            .WithoutPermission(Public)
            .SkipAudit("A read: the query travels in the body because a filter does not fit a URL.");

        endpoints.MapGet("/data/{table}/{id:guid}", (string table, Guid id, string? expand, RecordService records, IPluginContext context, CancellationToken ct) =>
                Run(records, context, ct, async () => await records.GetAsync(table, id, List(expand), RecordPlane.Public, ct) is { } record
                    ? Results.Ok(record)
                    : NotFound(table, id)))
            .WithoutPermission(Public);

        endpoints.MapGet("/data/{table}/{id:guid}/{navigation}", (string table, Guid id, string navigation, int? page, int? pageSize,
                RecordService records, IPluginContext context, CancellationToken ct) =>
                Run(records, context, ct, async () => await records.RelatedAsync(table, id, navigation, page ?? 1, pageSize ?? 50, RecordPlane.Public, ct) is { } related
                    ? Results.Ok(related)
                    : NotFound(table, id)))
            .WithoutPermission(Public);

        endpoints.MapPost("/data/{table}", (string table, JsonObject values, RecordService records, IPluginContext context, ISandboxContext sandbox,
                HttpContext http, CancellationToken ct) =>
                Write(records, context, http, ct, async () =>
                {
                    if (sandbox.IsSandbox)
                    {
                        return Preview();
                    }
                    var record = await records.CreateAsync(table, values, RecordPlane.Public, ct);
                    return Results.Created($"data/{table}/{record["id"]}", record);
                }))
            .WithoutPermission(Public)
            .AuditAs("record.created");

        // A manual flow, started from the site by a signed-in user holding flow:{name}:run on this
        // app. It runs as any manual flow does (definer's rights, ADR 0021); one that uses an action
        // needing a member permission cannot be started from here at all, since a site user holds none.
        endpoints.MapPost("/flows/{flow}/run", (string flow, StartFlowRequest? body, RecordService records, FlowRunService runs,
                IPluginContext context, ISandboxContext sandbox, HttpContext http, CancellationToken ct) =>
                Write(records, context, http, ct, async () =>
                {
                    if (!records.Members.Contains($"flow:{flow}:run"))
                    {
                        return Results.Json(new { error = $"Starting '{flow}' needs a role that allows it." },
                            statusCode: records.Visitor is null ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden);
                    }
                    if (sandbox.IsSandbox)
                    {
                        return Preview();
                    }
                    var input = body?.Input ?? [];
                    if (input.ToJsonString().Length > MaxFlowInput)
                    {
                        return Results.Json(new { error = "That input is too large." }, statusCode: StatusCodes.Status413PayloadTooLarge);
                    }
                    return Results.Accepted(value: new StartFlowResult(await runs.StartAsync(flow, input, ct)));
                }))
            .WithoutPermission("Started by a signed-in user holding the flow's run permission on this app (users.access@1), checked per call.")
            .AuditAs("flow.started");

        endpoints.MapPatch("/data/{table}/{id:guid}", (string table, Guid id, JsonObject values, RecordService records, IPluginContext context,
                ISandboxContext sandbox, HttpContext http, CancellationToken ct) =>
                Write(records, context, http, ct, async () => sandbox.IsSandbox
                    ? Preview()
                    : await records.UpdateAsync(table, id, values, RecordPlane.Public, ct) is { } record ? Results.Ok(record) : NotFound(table, id)))
            .WithoutPermission(Public)
            .AuditAs("record.updated");

        endpoints.MapDelete("/data/{table}/{id:guid}", (string table, Guid id, int? version, RecordService records, IPluginContext context,
                ISandboxContext sandbox, HttpContext http, CancellationToken ct) =>
                Write(records, context, http, ct, async () => sandbox.IsSandbox
                    ? Preview()
                    : await records.DeleteAsync(table, id, version, RecordPlane.Public, ct) ? Results.NoContent() : NotFound(table, id)))
            .WithoutPermission(Public)
            .AuditAs("record.deleted");
    }

    /// <summary>
    /// Says who the visitor is (a VisitorAuth visitor or a User Authentication user, when someone
    /// is signed in), what row rules can match for them, and what they hold on this app as a
    /// member, then runs the call.
    /// </summary>
    private static async Task<IResult> Run(RecordService records, IPluginContext context, CancellationToken ct, Func<Task<IResult>> handler)
    {
        var visitor = await context.Contracts.CurrentVisitorAsync(ct);
        records.Visitor = visitor?.Id;
        records.Subject = visitor is null ? null : await SubjectAsync(context, visitor, ct);
        (records.Members, records.Bypass) = await MemberPermissionsAsync(context, ct);
        try
        {
            return await RecordEndpoints.Run(handler);
        }
        catch (PublicAccessException e)
        {
            return Results.Json(new { error = e.Message }, statusCode: e.Status);
        }
    }


    /// <summary>
    /// The visitor as row rules may trust them. Email and groups only from a User Authentication
    /// user, whose identity provider or invitation vouched for the address; a VisitorAuth visitor
    /// picks theirs at registration. Attributes only those the visitor cannot edit themselves.
    /// </summary>
    private static async Task<RowSubject> SubjectAsync(IPluginContext context, VisitorProfile visitor, CancellationToken ct)
    {
        var user = context.Contracts.TryGet<IUserIdentity>() is { } identity && (await identity.GetCurrentAsync(ct)).User is { } u && u.Id == visitor.Id
            ? u
            : null;
        var attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (visitor.Attributes.Count > 0 && context.Contracts.TryGet<IVisitorProfiles>() is { } profiles)
        {
            var locked = (await profiles.ListAttributeDefinitionsAsync(ct)).Definitions.Where(d => !d.VisitorEditable).Select(d => d.Key).ToHashSet();
            foreach (var (key, value) in visitor.Attributes.Where(a => locked.Contains(a.Key)))
            {
                attributes[key] = value;
            }
        }
        return new RowSubject(visitor.Id, user?.Email, user?.Groups ?? [], attributes);
    }

    /// <summary>
    /// A public write, throttled: anyone may call these, and a table open for creation must not
    /// be a way to fill a tenant's database from one address.
    /// </summary>
    private static Task<IResult> Write(RecordService records, IPluginContext context, HttpContext http, CancellationToken ct, Func<Task<IResult>> handler) =>
        Run(records, context, ct, async () =>
        {
            // Every write counts against the address it comes from, and a signed-in visitor's
            // against them too; both must have room. Keyed by the visitor alone, one address
            // could register accounts and multiply its allowance.
            var cache = context.Contracts.Get<IPluginCache>();
            var app = context.Instance!.InstanceId.ToString("N");
            var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var used = (await cache.IncrementAsync(new CacheIncrement($"writes:{app}:ip:{address}", 1, 3600), ct)).Value;
            if (records.Visitor is { } visitor)
            {
                used = Math.Max(used, (await cache.IncrementAsync(new CacheIncrement($"writes:{app}:visitor:{visitor:N}", 1, 3600), ct)).Value);
            }
            return used > PublicEndpointsLimits.MaxWritesPerHour
                ? Results.Json(new { error = "Too many changes from here for now; try again within the hour." }, statusCode: 429)
                : await handler();
        });

    /// <summary>
    /// The signed-in user's site permissions on this app (users.access@1), and those of them held
    /// through a role that bypasses row-level access, their <c>dynamic-apps:{slug}:</c> prefix stripped.
    /// </summary>
    private static async Task<(IReadOnlySet<string> Members, IReadOnlySet<string> Bypass)> MemberPermissionsAsync(IPluginContext context, CancellationToken ct)
    {
        if (context.Contracts.TryGet<IUserAccess>() is not { } access || context.Instance is not { } instance)
        {
            return (new HashSet<string>(), new HashSet<string>());
        }
        var prefix = $"{DynamicAppsPlugin.PluginId}:{instance.Slug}:";
        HashSet<string> Ours(IEnumerable<string> permissions) => permissions
            .Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
            .Select(p => p[prefix.Length..])
            .ToHashSet(StringComparer.Ordinal);
        var held = await access.ListPermissionsAsync(ct);
        return (Ours(held.Permissions), Ours(held.BypassRowAccess ?? []));
    }

    // A preview of the site is not the site: its writes would land in the real application.
    private static IResult Preview() => Results.Json(new { error = "A site preview cannot change the app's records." }, statusCode: 403);

    private static IReadOnlyList<string> List(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? [] : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IResult NotFound(string table, Guid id) => Results.NotFound(new { error = $"There is no {table} record {id}." });
}

public static class PublicEndpointsLimits
{
    /// <summary>Writes from the public site, per app and per client (a visitor, else an address), in an hour.</summary>
    public const int MaxWritesPerHour = 120;
}
