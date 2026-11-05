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

public sealed record LiveFunction(string Name, string? Description, IReadOnlyList<string> Access);

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
                .Select(f => new LiveFunction(f.Name, f.Description, f.Http))
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

    /// <summary>Each HTTP function as <c>GET</c> (input from the query) and <c>POST</c> (input from the JSON body).</summary>
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
            paths.Add(new OpenApiPathFragment($"/fn/{function.Name}", "get", $"{id}_get",
                $"Call {function.Name}", description + "\n\nThe query string is the input.", new JsonObject(),
                Parameters: parameters, ClientPath: ["fn", .. segments, "get"]));
            paths.Add(new OpenApiPathFragment($"/fn/{function.Name}", "post", $"{id}_post",
                $"Call {function.Name}", description, new JsonObject(), RequestBodySchema: new JsonObject(),
                Parameters: parameters, ClientPath: ["fn", .. segments, "post"]));
        }
        return new OpenApiFragment(instance.Name, $"{instance.Description}\n\n{manifest.Description}", paths, new Dictionary<string, JsonNode>());
    }
}
