using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Data;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Runtime.Data;

/// <summary>
/// Serves every plugin's data sets to the admin console, under the instance's admin prefix:
/// <c>/api/admin/plugins/{slug}/_data</c>. One set of routes for all plugins — the slug picks the
/// plugin — so the console renders any data set, including an installed plugin's, the same way.
///
/// <para>The runtime owns the gate, not the plugin: the caller must hold the set's read (or
/// write) permission, only what <see cref="DataSetSchema"/> says the set supports is called,
/// values are cut down to the item schema's properties and validated against it, filters to
/// declared options, sorts to sortable columns. Every change is audited against the instance.
/// Rows are addressed by <c>?key=</c> because keys may contain '/'.</para>
/// </summary>
public static class PluginDataEndpoints
{
    public const int MaxPageSize = 100;
    public const int MaxActionKeys = 500;

    public static IEndpointRouteBuilder MapDcmsPluginDataEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/plugins/{slug}/_data")
            .RequireAuthorization()
            .PermissionExempt("Each data set declares its own read and write permission, checked per request.");

        group.MapGet("", ListSetsAsync);
        group.MapGet("/{set}", QueryAsync);
        group.MapGet("/{set}/row", GetRowAsync);
        group.MapGet("/{set}/download", DownloadAsync);
        group.MapPost("/{set}/rows", CreateAsync)
            .AuditExempt("Records plugin.data.created itself, naming the instance, data set and row.");
        group.MapPut("/{set}/row", UpdateAsync)
            .AuditExempt("Records plugin.data.updated itself, naming the instance, data set and row.");
        group.MapDelete("/{set}/row", DeleteAsync)
            .AuditExempt("Records plugin.data.deleted itself, naming the instance, data set and row.");
        group.MapPost("/{set}/actions/{action}", ActionAsync)
            .AuditExempt("Records plugin.data.actioned itself, naming the instance, data set, action and rows.");
        return app;
    }

    /// <summary>The instance's data sets the caller may read, each with its schema.</summary>
    private static async Task<IResult> ListSetsAsync(string slug, HttpContext http, CancellationToken ct)
    {
        if (await InstanceAsync(http, slug, ct) is not { } found)
        {
            return Results.NotFound();
        }
        var result = new List<object>();
        foreach (var declaration in PluginDataSets.Of(found.Plugin.Manifest))
        {
            if (!await MayAsync(http, ReadPermission(found.Plugin.Manifest.Id, declaration)))
            {
                continue;
            }
            var set = await OpenAsync(http, found, declaration, ct);
            result.Add(Describe(declaration, await set.DescribeAsync(ct), await MayAsync(http, WritePermission(found.Plugin.Manifest.Id, declaration))));
        }
        return Results.Ok(result);
    }

    private static Task<IResult> QueryAsync(string slug, string set, HttpContext http, CancellationToken ct) =>
        RunAsync(http, slug, set, write: false, ct, async (data, schema, _) =>
        {
            var q = http.Request.Query;
            var sortable = schema.Columns.Where(c => c.Sortable).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            var sort = q["sort"].ToString() is { Length: > 0 } s && sortable.Contains(s) ? s : schema.DefaultSort;
            var descending = q.ContainsKey("desc") ? q["desc"] == "true" : schema.DefaultDescending && sort == schema.DefaultSort;

            // Only declared filters, only declared options: a plugin never sees a value it did not offer.
            var filters = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var filter in schema.Filters ?? [])
            {
                if (q[$"f.{filter.Key}"].ToString() is { Length: > 0 } value && filter.Options.Any(o => o.Value == value))
                {
                    filters[filter.Key] = value;
                }
            }

            var query = new DataQuery(
                Search: schema.Searchable && q["search"].ToString() is { Length: > 0 } search ? search.Trim() : null,
                Sort: sort,
                Descending: descending,
                Filters: filters,
                Page: Math.Max(int.TryParse(q["page"], out var page) ? page : 1, 1),
                PageSize: Math.Clamp(int.TryParse(q["pageSize"], out var size) ? size : 25, 1, MaxPageSize));
            var result = await data.ListAsync(query, ct);
            return Results.Ok(new { rows = result.Rows, total = result.Total, page = query.Page, pageSize = query.PageSize });
        });

    private static Task<IResult> GetRowAsync(string slug, string set, string key, HttpContext http, CancellationToken ct) =>
        RunAsync(http, slug, set, write: false, ct, async (data, _, _) =>
            await data.GetAsync(key, ct) is { } row ? Results.Ok(row) : Results.NotFound());

    private static Task<IResult> DownloadAsync(string slug, string set, string key, HttpContext http, CancellationToken ct) =>
        RunAsync(http, slug, set, write: false, ct, async (data, schema, _) =>
        {
            if (!schema.CanDownload)
            {
                return NotSupported();
            }
            // Always an attachment: a plugin's file is never rendered in the console's origin.
            return await data.DownloadAsync(key, ct) is { } file
                ? Results.File(file.Content, file.ContentType, file.FileName)
                : Results.NotFound();
        });

    private static Task<IResult> CreateAsync(string slug, string set, HttpContext http, CancellationToken ct) =>
        RunAsync(http, slug, set, write: true, ct, async (data, schema, audit) =>
        {
            if (!schema.CanCreate)
            {
                return NotSupported();
            }
            var values = await ValuesAsync(http, schema.ItemSchema, ct);
            var row = await data.CreateAsync(values, ct);
            audit(AuditActions.PluginDataCreated, row.Key, null);
            return Results.Ok(row);
        });

    private static Task<IResult> UpdateAsync(string slug, string set, string key, HttpContext http, CancellationToken ct) =>
        RunAsync(http, slug, set, write: true, ct, async (data, schema, audit) =>
        {
            if (!schema.CanUpdate)
            {
                return NotSupported();
            }
            var values = await ValuesAsync(http, schema.ItemSchema, ct);
            if (await data.UpdateAsync(key, values, ct) is not { } row)
            {
                return Results.NotFound();
            }
            audit(AuditActions.PluginDataUpdated, key, null);
            return Results.Ok(row);
        });

    private static Task<IResult> DeleteAsync(string slug, string set, string key, HttpContext http, CancellationToken ct) =>
        RunAsync(http, slug, set, write: true, ct, async (data, schema, audit) =>
        {
            if (!schema.CanDelete)
            {
                return NotSupported();
            }
            if (!await data.DeleteAsync(key, ct))
            {
                return Results.NotFound();
            }
            audit(AuditActions.PluginDataDeleted, key, null);
            return Results.NoContent();
        });

    private static Task<IResult> ActionAsync(string slug, string set, string action, HttpContext http, CancellationToken ct) =>
        RunAsync(http, slug, set, write: true, ct, async (data, schema, audit) =>
        {
            if ((schema.Actions ?? []).FirstOrDefault(a => a.Id == action) is not { } declared)
            {
                return Results.NotFound();
            }
            var body = await ReadObjectAsync(http, ct);
            var keys = (body["keys"] as JsonArray ?? [])
                .Select(k => k?.GetValueKind() == JsonValueKind.String ? k.GetValue<string>() : null)
                .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            if (keys.Count == 0 || keys.Count > MaxActionKeys || (!declared.Bulk && keys.Count != 1))
            {
                return Results.BadRequest(new
                {
                    error = declared.Bulk
                        ? $"Select between 1 and {MaxActionKeys} rows."
                        : $"'{declared.Label}' runs on one row at a time.",
                });
            }
            var input = body["input"] as JsonObject;
            if (declared.InputSchema is not null)
            {
                input = Validated(Restrict(input ?? [], declared.InputSchema), declared.InputSchema, http);
            }
            var result = await data.RunActionAsync(action, keys, input, ct);
            audit(AuditActions.PluginDataActioned, keys.Count == 1 ? keys[0] : null, entry => entry
                .With("action", action)
                .With("rows", keys.Count)
                .With("affected", result.Affected));
            return Results.Ok(result);
        });

    /* ---- plumbing ---- */

    private sealed record FoundInstance(Guid TenantId, PluginInstanceContext Instance, IPlugin Plugin);

    /// <summary>An instance of any plugin, enabled or not, by slug; null (404) for anything else.</summary>
    private static async Task<FoundInstance?> InstanceAsync(HttpContext http, string slug, CancellationToken ct)
    {
        var services = http.RequestServices;
        if (services.GetRequiredService<ITenantContext>().TenantId is not { } tenantId)
        {
            return null;
        }
        // Enabled or not: a switched-off plugin's data stays reachable for the people who own it.
        var instance = await services.GetRequiredService<PluginContextFactory>().FindAnyInstanceAsync(tenantId, slug, ct);
        return instance is not null && services.GetRequiredService<PluginRegistry>().FindPlugin(instance.PluginId) is { } plugin
            ? new FoundInstance(tenantId, instance, plugin)
            : null;
    }

    /// <summary>Builds the data set with the plugin's own context for this instance, as a plugin route would have it.</summary>
    private static async Task<IPluginDataSet> OpenAsync(
        HttpContext http, FoundInstance found, DataSetDeclaration declaration, CancellationToken ct)
    {
        var services = http.RequestServices;
        var factory = services.GetRequiredService<PluginContextFactory>();
        var context = await factory.CreateAsync(found.TenantId, found.Plugin.Manifest.Id, found.Instance, factory.CurrentActor(), ct);
        services.GetRequiredService<PluginContextAccessor>().Current = context;
        return (IPluginDataSet)ContractActivator.Create(services, declaration.Implementation, context);
    }

    private static async Task<IResult> RunAsync(
        HttpContext http, string slug, string setId, bool write, CancellationToken ct,
        Func<IPluginDataSet, DataSetSchema, Action<string, string?, Action<AuditEntry>?>, Task<IResult>> handler)
    {
        if (await InstanceAsync(http, slug, ct) is not { } found
            || PluginDataSets.Of(found.Plugin.Manifest).FirstOrDefault(s => s.Id == setId) is not { } declaration)
        {
            return Results.NotFound();
        }
        if (!await MayAsync(http, ReadPermission(found.Plugin.Manifest.Id, declaration)))
        {
            return Results.NotFound(); // a set the caller may not read is not listed either
        }
        if (write && !await MayAsync(http, WritePermission(found.Plugin.Manifest.Id, declaration)))
        {
            return Results.Json(new { error = "You may view this data but not change it." }, statusCode: StatusCodes.Status403Forbidden);
        }

        void Audit(string action, string? key, Action<AuditEntry>? more)
        {
            var entry = http.RequestServices.GetRequiredService<IAuditRecorder>()
                .Record(action)
                .For("plugin_instance", found.Instance.InstanceId, found.Instance.Name)
                .With("plugin", found.Plugin.Manifest.Id)
                .With("dataSet", declaration.Id)
                .With("key", key);
            more?.Invoke(entry);
        }

        try
        {
            var data = await OpenAsync(http, found, declaration, ct);
            return await handler(data, await data.DescribeAsync(ct), Audit);
        }
        catch (BadValuesException e)
        {
            return Results.BadRequest(new { error = e.Message, details = e.Details });
        }
        catch (ContractValidationException e)
        {
            return Results.BadRequest(new { error = e.Message });
        }
        catch (ContractConflictException e)
        {
            return Results.Conflict(new { error = e.Message });
        }
        catch (NotSupportedException)
        {
            return NotSupported();
        }
    }

    private static IResult NotSupported() =>
        Results.Json(new { error = "This data set does not support that." }, statusCode: StatusCodes.Status405MethodNotAllowed);

    internal static string ReadPermission(string pluginId, DataSetDeclaration set) =>
        PluginPermissions.Resolve(pluginId, set.ReadPermission ?? PlatformPermissions.PluginsManage);

    internal static string WritePermission(string pluginId, DataSetDeclaration set) =>
        PluginPermissions.Resolve(pluginId, set.WritePermission ?? PlatformPermissions.PluginsManage);

    private static async Task<bool> MayAsync(HttpContext http, string permission)
    {
        var authz = http.RequestServices.GetRequiredService<IAuthorizationService>();
        return (await authz.AuthorizeAsync(http.User, null, PermissionPolicyProvider.PolicyName(permission))).Succeeded;
    }

    private static async Task<JsonObject> ValuesAsync(HttpContext http, JsonObject? itemSchema, CancellationToken ct)
    {
        var body = await ReadObjectAsync(http, ct);
        return itemSchema is null ? body : Validated(Restrict(body, itemSchema), itemSchema, http);
    }

    /// <summary>Only the schema's own properties reach the plugin.</summary>
    private static JsonObject Restrict(JsonObject values, JsonObject schema)
    {
        var allowed = (schema["properties"] as JsonObject)?.Select(p => p.Key).ToHashSet(StringComparer.Ordinal) ?? [];
        var result = new JsonObject();
        foreach (var (key, value) in values)
        {
            if (allowed.Contains(key))
            {
                result[key] = value?.DeepClone();
            }
        }
        return result;
    }

    private static JsonObject Validated(JsonObject values, JsonObject schema, HttpContext http)
    {
        var (valid, errors) = http.RequestServices.GetRequiredService<PluginConfigValidator>()
            .Validate(schema.ToJsonString(), values.ToJsonString());
        return valid ? values : throw new BadValuesException(errors);
    }

    private static async Task<JsonObject> ReadObjectAsync(HttpContext http, CancellationToken ct)
    {
        try
        {
            return await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct) as JsonObject
                ?? throw new ContractValidationException("The body must be a JSON object.");
        }
        catch (JsonException)
        {
            throw new ContractValidationException("The body must be a JSON object.");
        }
    }

    private static object Describe(DataSetDeclaration declaration, DataSetSchema schema, bool canWrite) => new
    {
        id = declaration.Id,
        title = declaration.Title,
        description = declaration.Description,
        icon = declaration.IconName,
        platform = !RegistryIsKebab(declaration.Id),
        canWrite,
        columns = schema.Columns,
        itemSchema = schema.ItemSchema,
        filters = schema.Filters ?? [],
        actions = (schema.Actions ?? []).Select(a => new
        {
            id = a.Id,
            label = a.Label,
            risk = a.Risk.ToString().ToLowerInvariant(),
            bulk = a.Bulk,
            description = a.Description,
            inputSchema = a.InputSchema,
        }),
        searchable = schema.Searchable,
        canCreate = schema.CanCreate && canWrite,
        canUpdate = schema.CanUpdate && canWrite,
        canDelete = schema.CanDelete && canWrite,
        canDownload = schema.CanDownload,
        defaultSort = schema.DefaultSort,
        defaultDescending = schema.DefaultDescending,
    };

    // Plugin-declared ids are kebab-case; the platform's are contract names ("dcms.storage").
    private static bool RegistryIsKebab(string id) => PluginRegistry.IsKebabCase(id);

    private sealed class BadValuesException(IReadOnlyList<string> details) : Exception("The values do not match the data set's schema.")
    {
        public IReadOnlyList<string> Details => details;
    }
}
