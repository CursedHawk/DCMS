using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.Functions.Build;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Functions;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Functions.Http;

/// <param name="Methods">The methods it answers, lowercase (none: GET and POST, for documents cached before they were sent).</param>
/// <param name="Input">What it takes, as JSON Schema; null is any JSON.</param>
/// <param name="Output">What it returns, likewise.</param>
public sealed record LiveFunction(string Name, string? Description, IReadOnlyList<string> Access,
    IReadOnlyList<string>? Methods = null, JsonObject? Input = null, JsonObject? Output = null);

/// <summary>The live deployment's number and its functions callable over HTTP; number 0 when nothing is live.</summary>
public sealed record LiveHttp(int Deployment, IReadOnlyList<LiveFunction> Functions);

public sealed record LiveHttpRequest(Guid TenantId, Guid EnvironmentId, Guid InstanceId);

/// <summary>
/// The HTTP functions in the tenant's OpenAPI document and generated client (ADR 0029). Admin-api
/// reads the live deployment; content-api, which cannot read the <c>functions</c> schema, asks
/// admin-api through the service route the HTTP calls already use, and remembers the answer briefly.
/// </summary>
public static class LiveFunctions
{
    public const string Route = "/api/internal/functions/live";

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost(Route, async (LiveHttpRequest body, FunctionsDbContext db, CancellationToken ct) =>
                Results.Ok(await ReadAsync(db, body.TenantId, body.EnvironmentId, body.InstanceId, ct)))
            .RequireAuthorization()
            .AllowServicePrincipal(InvokeEndpoint.Scope,
                "content-api describes a tenant's HTTP functions in its OpenAPI document; the deployments are here.")
            .PermissionExempt("A service route: the live HTTP functions' names and declared access, which the document publishes anyway.")
            .SkipAudit("Reads only.");

    public static async Task<LiveHttp> ReadAsync(FunctionsDbContext db, Guid tenantId, Guid environmentId, Guid instanceId, CancellationToken ct)
    {
        using var rls = RlsScope.Environment(tenantId, environmentId);
        var live = await db.Deployments.AsNoTracking()
            .Where(d => d.InstanceId == instanceId && d.Status == FunctionDeploymentStatus.Active)
            .Select(d => new { d.Number, d.Manifest })
            .FirstOrDefaultAsync(ct);
        return live is null
            ? new LiveHttp(0, [])
            : new LiveHttp(live.Number, FunctionManifest.Deserialize(live.Manifest)
                .Where(f => f.Http.Count > 0)
                .Select(f => new LiveFunction(f.Name, f.Description, f.Http, f.HttpMethods, f.Input, f.Output))
                .ToList());
    }

    /// <summary>The live HTTP functions on either plane; null when admin-api could not say.</summary>
    public static async Task<LiveHttp?> GetAsync(PluginInstanceContext instance, IServiceProvider services, CancellationToken ct)
    {
        if (services.GetService<FunctionsDbContext>() is { } db)
        {
            return await ReadAsync(db, instance.TenantId, instance.EnvironmentId, instance.InstanceId, ct);
        }

        // ponytail: a deploy shows in content-api's document up to 30 s late; push an eviction if that matters.
        var cache = services.GetRequiredService<IMemoryCache>();
        var key = $"fn:live:{instance.InstanceId:N}";
        if (cache.TryGetValue(key, out LiveHttp? known))
        {
            return known;
        }
        try
        {
            using var call = new HttpRequestMessage(HttpMethod.Post, Route)
            {
                Content = JsonContent.Create(new LiveHttpRequest(instance.TenantId, instance.EnvironmentId, instance.InstanceId)),
            };
            call.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                await services.GetRequiredService<IServiceTokenProvider>().GetTokenAsync(InvokeEndpoint.Scope, ct));
            using var response = await services.GetRequiredService<IHttpClientFactory>().CreateClient(SiteEndpoints.HttpClientName).SendAsync(call, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var live = await response.Content.ReadFromJsonAsync<LiveHttp>(ct);
            cache.Set(key, live, TimeSpan.FromSeconds(30));
            return live;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // The document leaves the functions out until admin-api answers again.
            return null;
        }
    }

    /// <summary>
    /// Each HTTP function at each method it answers: GET and DELETE take the input as query
    /// parameters, the others as the JSON body; the answer is its output. Both as their types say.
    /// </summary>
    public static OpenApiFragment Fragment(PluginInstanceContext instance, LiveHttp? live, PluginManifest manifest)
    {
        var paths = new List<OpenApiPathFragment>();
        foreach (var function in live?.Functions ?? [])
        {
            var who = string.Join(", ", function.Access.Select(a => a switch
            {
                "public" => "anyone",
                "user" => "a signed-in site user",
                "role" => "a site user whose role grants it",
                "apiKey" => $"an API key in {ApiKeyService.Header}",
                _ => a,
            }));
            var description = $"{instance.Description}\n\n{function.Description ?? $"The {function.Name} function."} Callable by {who}. "
                              + $"Add ?async=1 to run it later: 202 with a job id.\n\nPlugin: {manifest.Name} v{manifest.Version}";
            IReadOnlyList<JsonNode>? parameters = function.Access.Contains("apiKey")
                ? [new JsonObject
                {
                    ["name"] = ApiKeyService.Header, ["in"] = "header", ["required"] = function.Access.Count == 1,
                    ["schema"] = new JsonObject { ["type"] = "string" },
                }]
                : null;
            // "orders/total" is /fn/orders/total, operation …_fn_orders_total_get, client fn.orders.total.get.
            var segments = function.Name.Split('/');
            var id = $"{instance.Slug}_fn_{string.Join('_', segments)}";
            var output = function.Output?.DeepClone() ?? new JsonObject();
            foreach (var method in function.Methods is { Count: > 0 } declared ? declared : FunctionManifest.DefaultMethods)
            {
                var query = method is "get" or "delete";
                IReadOnlyList<JsonNode>? all = query ? [.. parameters ?? [], .. QueryParameters(function.Input)] : parameters;
                paths.Add(new OpenApiPathFragment($"/fn/{function.Name}", method, $"{id}_{method}",
                    $"Call {function.Name}", query ? description + "\n\nThe query string is the input." : description, output.DeepClone(),
                    RequestBodySchema: query ? null : function.Input?.DeepClone() ?? new JsonObject(),
                    Parameters: all is { Count: > 0 } ? all : null, ClientPath: ["fn", .. segments, method]));
            }
        }
        return new OpenApiFragment(instance.Name, $"{instance.Description}\n\n{manifest.Description}", paths, new Dictionary<string, JsonNode>());
    }

    /// <summary>An object input's properties, as the query parameters a GET or DELETE sends it in.</summary>
    private static IEnumerable<JsonNode> QueryParameters(JsonObject? input)
    {
        if (input?["properties"] is not JsonObject properties) yield break;
        var required = (input["required"] as JsonArray ?? []).Select(r => r?.GetValue<string>()).ToHashSet();
        foreach (var (name, schema) in properties)
        {
            var parameter = new JsonObject { ["name"] = name, ["in"] = "query", ["required"] = required.Contains(name), ["schema"] = schema?.DeepClone() };
            if (schema?["description"] is JsonValue d) parameter["description"] = d.DeepClone();
            yield return parameter;
        }
    }
}
