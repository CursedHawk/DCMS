using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Dcms.Plugins.Functions.Build;
using Dcms.Plugins.Functions.Runtime;
using Dcms.PluginSdk.Runtime.Hosting;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Functions;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Functions.Http;

/// <summary>The signed-in site user content-api found on the request, as it vouches for them.</summary>
public sealed record InvokeUser(Guid Id, string? Email, IReadOnlyList<Guid> Groups);

/// <param name="RoleGranted">Whether a role grants the user <c>invoke</c> on this function (users.access@1, asked by content-api).</param>
/// <param name="ApiKey">The <c>X-Api-Key</c> the caller sent, verified here, where the keys are.</param>
public sealed record InvokeCaller(InvokeUser? User, bool RoleGranted, string? ApiKey);

public sealed record InvokeRequest(
    Guid TenantId, Guid EnvironmentId, Guid InstanceId, string Function,
    string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers,
    JsonNode? Body, InvokeCaller Caller, bool Async = false);

/// <summary>What content-api writes back: the function's own status, headers and body.</summary>
public sealed record InvokeResponse(int Status, Dictionary<string, string> Headers, JsonNode? Body, string? Text);

/// <summary>
/// HTTP invocation, admin side (ADR 0029): content-api authenticates the caller and forwards
/// here, because the broker needs what only admin-api holds (secrets, Transit). The token is
/// client-credentials with <c>dcms.functions</c>, which opens this endpoint and nothing else;
/// the path is a naming convention, not a boundary (the edge routes <c>/api/*</c> here). The
/// access the function declared is decided here, against the live deployment.
/// </summary>
public static class InvokeEndpoint
{
    public const string Route = "/api/internal/functions/invoke";
    public const string Scope = "dcms.functions";

    /// <summary>HTTP runs one tenant may have going at once on this replica: the rest wait briefly, then get 503.</summary>
    public const int PerTenant = 4;

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Running = new();

    /// <summary>Headers a function's answer may set; anything else (cookies, CORS, security headers) is the platform's.</summary>
    public static readonly HashSet<string> ResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "content-type", "cache-control", "location", "etag", "last-modified", "content-disposition", "content-language", "vary",
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(Route, (InvokeRequest body, IServiceProvider services, CancellationToken ct) => InvokeAsync(body, services, ct))
            .RequireAuthorization()
            .AllowServicePrincipal(Scope,
                "content-api serves a tenant's HTTP functions and holds no secrets; the broker that runs them is here.")
            // The caller is content-api, not a member: access is the function's own declaration, decided below.
            .PermissionExempt("A service route: the function's declared access is checked against the caller content-api vouches for.")
            .SkipAudit("A failed run is in the run history and a run's writes are audited by their owners; "
                       + "a record per public call would bury the trail under traffic.");
    }

    private static async Task<IResult> InvokeAsync(InvokeRequest body, IServiceProvider services, CancellationToken ct)
    {
        using var rls = RlsScope.Environment(body.TenantId, body.EnvironmentId);
        IResult result = Results.NotFound(new { error = "There is no such function." });
        // The instance must be this tenant's, enabled, and Functions: the runner checks all three.
        var ran = await services.GetRequiredService<PluginHandlerRunner>().RunAsync(body.TenantId, FunctionsPlugin.PluginId, body.InstanceId,
            async (context, sp) => result = await RunAsync(body, context, sp, ct), ct);
        return ran ? result : Results.NotFound(new { error = "There is no such function." });
    }

    private static async Task<IResult> RunAsync(InvokeRequest body, PluginSdk.Abstractions.Contracts.IPluginContext context, IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<FunctionsDbContext>();
        var live = await db.Deployments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.InstanceId == body.InstanceId && d.Status == FunctionDeploymentStatus.Active, ct);
        var target = live is null ? null : RunTargets.Of(live, body.Function, live.VouchedPermissions);
        if (target is null || target.Function.Http.Count == 0)
        {
            // Not deployed, or not callable over HTTP: the same answer, so nothing is learnt by asking.
            return Results.NotFound(new { error = "There is no such function." });
        }

        var http = target.Function.Http;
        var caller = body.Caller;
        var allowed = http.Contains("public")
                      || (caller.User is not null && http.Contains("user"))
                      || (caller.User is not null && caller.RoleGranted && http.Contains("role"))
                      || (http.Contains("apiKey") && await ApiKeyService.VerifyAsync(db, body.InstanceId, caller.ApiKey, body.Function, ct));
        if (!allowed)
        {
            var offered = caller.User is not null || caller.ApiKey is not null;
            return offered
                ? Results.Json(new { error = "These credentials may not call this function." }, statusCode: StatusCodes.Status403Forbidden)
                : Results.Json(new { error = "Sign in, or send an API key in X-Api-Key." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        // What the function receives: a POST's body, else the query string.
        var input = body.Method == "POST" ? body.Body?.DeepClone() : JsonNodeOf(body.Query);
        if (body.Async)
        {
            var ticket = await RunJob.EnqueueAsync(context, body.InstanceId, body.Function, input, dedupeKey: null, generation: 1, ct);
            return Results.Json(new { jobId = ticket.JobId }, statusCode: StatusCodes.Status202Accepted);
        }

        var gate = Running.GetOrAdd(body.TenantId, _ => new SemaphoreSlim(PerTenant));
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(2), ct))
        {
            return Results.Json(new { error = "Too many calls are running for this site right now; try again shortly." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        RunOutcome outcome;
        try
        {
            var request = new JsonObject
            {
                ["method"] = body.Method,
                ["path"] = body.Path,
                ["query"] = JsonNodeOf(body.Query),
                ["headers"] = JsonNodeOf(body.Headers),
                ["body"] = body.Body?.DeepClone(),
            };
            var user = caller.User is { } u
                ? new JsonObject { ["id"] = u.Id.ToString(), ["email"] = u.Email, ["groups"] = new JsonArray(u.Groups.Select(g => (JsonNode?)g.ToString()).ToArray()) }
                : null;
            outcome = await sp.GetRequiredService<FunctionRunner>().RunAsync(target, input,
                new RunTrigger("http", new JsonObject { ["method"] = body.Method, ["path"] = body.Path }), ct, request, user);
        }
        finally
        {
            gate.Release();
        }

        if (!outcome.Succeeded)
        {
            // ponytail: only failed HTTP runs are recorded; recording every call of a public endpoint would fill the history.
            await sp.GetRequiredService<RunQueue>().RecordAsync(target, "http", input, outcome, ct);
            return Results.Json(new { error = "The function failed." }, statusCode: StatusCodes.Status500InternalServerError);
        }
        return Results.Ok(Answer(outcome.Result));
    }

    /// <summary>The function's <c>respond(…)</c>, or its value as JSON with 200.</summary>
    public static InvokeResponse Answer(JsonNode? result)
    {
        if (result is JsonObject o && o["$response"] is JsonObject r)
        {
            var status = r["status"] is JsonValue s && s.TryGetValue<int>(out var code) && code is >= 200 and <= 599 ? code : 200;
            var headers = (r["headers"] as JsonObject ?? [])
                .Where(h => ResponseHeaders.Contains(h.Key) && h.Value is JsonValue v && v.TryGetValue<string>(out var text) && text.Length <= 2048
                            && !text.Contains('\r') && !text.Contains('\n'))
                .ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value!.GetValue<string>());
            return r["body"] is JsonValue b && b.TryGetValue<string>(out var str)
                ? new InvokeResponse(status, headers, null, str)
                : new InvokeResponse(status, headers, r["body"]?.DeepClone(), null);
        }
        return new InvokeResponse(200, new Dictionary<string, string>(), result?.DeepClone(), null);
    }

    private static JsonObject JsonNodeOf(Dictionary<string, string> values) =>
        new(values.Select(v => KeyValuePair.Create(v.Key, (JsonNode?)v.Value)));
}
