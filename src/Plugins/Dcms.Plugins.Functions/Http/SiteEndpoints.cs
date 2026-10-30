using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dcms.Plugins.UserAuth.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.Functions.Http;

/// <summary>
/// <c>GET|POST /api/{app}/fn/{function}</c> on the tenant's sites (ADR 0029), in content-api.
/// It establishes who is calling — the signed-in site user and whether a role grants them this
/// function, and any API key — throttles, bounds the body, and forwards to admin-api, which
/// decides against the live deployment and runs it. <c>fn</c> is a literal segment, so content
/// delivery's <c>{contentType}</c> routes never see these.
/// </summary>
public static partial class SiteEndpoints
{
    public const string HttpClientName = "functions-admin-api";
    public const int MaxBodyBytes = 1024 * 1024;
    public const int PerAddressPerMinute = 120;
    public const int PerAppPerMinute = 3000;

    /// <summary>Request headers a function sees; credentials, cookies and the platform's own never.</summary>
    private static readonly HashSet<string> RequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept", "accept-language", "content-type", "user-agent", "origin", "referer", "if-none-match", "if-modified-since",
    };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex FunctionName();

    public static void Map(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapGet("/fn/{function}", InvokeAsync).PermissionExempt(Exempt);
        endpoints.MapPost("/fn/{function}", InvokeAsync).PermissionExempt(Exempt).SkipAudit(
            "Each function's access is its own declaration, checked by admin-api; a failed run is in the run history.");
    }

    private const string Exempt = "Public by design: each function declares who may call it, which admin-api checks against the live deployment.";

    private static async Task<IResult> InvokeAsync(
        string function, HttpContext http, IPluginContext context, IServiceTokenProvider tokens, IHttpClientFactory clients,
        ILoggerFactory loggers, CancellationToken ct)
    {
        if (!FunctionName().IsMatch(function) || context.Instance is not { } instance)
        {
            return Results.NotFound(new { error = "There is no such function." });
        }

        // Throttled per address and per app: anyone may call, and a function costs a sandbox.
        var cache = context.Contracts.Get<IPluginCache>();
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        async Task<long> Count(string key) => (await cache.IncrementAsync(new CacheIncrement($"fn:{instance.InstanceId:N}:{key}:{minute}", 1, 120), ct)).Value;
        if (await Count($"ip:{address}") > PerAddressPerMinute || await Count("all") > PerAppPerMinute)
        {
            return Results.Json(new { error = "Too many calls from here; try again in a minute." }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        JsonNode? body = null;
        if (HttpMethods.IsPost(http.Request.Method))
        {
            var bytes = await ReadBoundedAsync(http.Request.Body, ct);
            if (bytes is null)
            {
                return Results.Json(new { error = $"A request body is at most {MaxBodyBytes / 1024} KiB." }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
            var text = Encoding.UTF8.GetString(bytes);
            if (http.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true && text.Length > 0)
            {
                try
                {
                    body = JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "The body is not valid JSON." });
                }
            }
            else if (text.Length > 0)
            {
                body = JsonValue.Create(text);
            }
        }

        // Who is calling, as this plane knows it: admin-api trusts what this service vouches for.
        var user = context.Contracts.TryGet<IUserIdentity>() is { } identity ? (await identity.GetCurrentAsync(ct)).User : null;
        var permission = SitePermission.Of(FunctionsPlugin.PluginId, instance.Slug, $"fn:{function.ToLowerInvariant()}", "invoke");
        var roleGranted = user is not null && SitePermission.IsValid(permission) && context.Contracts.TryGet<IUserAccess>() is { } access
                          && (await access.CheckAsync(new PermissionCheck(permission), ct)).Allowed;
        var apiKey = http.Request.Headers[ApiKeyService.Header].ToString() is { Length: > 0 and <= 200 } k ? k : null;

        var request = new InvokeRequest(
            context.TenantId, context.EnvironmentId, instance.InstanceId, function,
            http.Request.Method.ToUpperInvariant(), http.Request.Path.Value ?? "",
            http.Request.Query.Take(50).ToDictionary(q => q.Key, q => q.Value.ToString()),
            http.Request.Headers.Where(h => RequestHeaders.Contains(h.Key) || (h.Key.StartsWith("x-", StringComparison.OrdinalIgnoreCase) && Forwardable(h.Key)))
                .Take(30).ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString() is { Length: > 2048 } v ? v[..2048] : h.Value.ToString()),
            body,
            new InvokeCaller(user is null ? null : new InvokeUser(user.Id, user.Email, user.Groups), roleGranted, apiKey),
            Async: http.Request.Query["async"] == "1");

        HttpResponseMessage response;
        try
        {
            using var call = new HttpRequestMessage(HttpMethod.Post, InvokeEndpoint.Route) { Content = JsonContent.Create(request) };
            call.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(InvokeEndpoint.Scope, ct));
            response = await clients.CreateClient(HttpClientName).SendAsync(call, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            loggers.CreateLogger("Dcms.Plugins.Functions.Http").LogWarning(e, "Could not reach admin-api to invoke {Function}.", function);
            return Results.Json(new { error = "The function could not be reached. Try again shortly." }, statusCode: StatusCodes.Status502BadGateway);
        }

        using (response)
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode || (int)response.StatusCode == StatusCodes.Status202Accepted)
            {
                // Refusals and failures from admin-api: their status and their sentence.
                return Results.Content(json, "application/json", Encoding.UTF8, (int)response.StatusCode);
            }
            var answer = JsonSerializer.Deserialize<InvokeResponse>(json, JsonSerializerOptions.Web)!;
            foreach (var (name, value) in answer.Headers.Where(h => InvokeEndpoint.ResponseHeaders.Contains(h.Key)))
            {
                http.Response.Headers[name] = value;
            }
            if (answer.Text is { } text)
            {
                var type = answer.Headers.GetValueOrDefault("content-type") ?? "text/plain; charset=utf-8";
                return Results.Content(text, type, Encoding.UTF8, answer.Status);
            }
            return Results.Json(answer.Body, statusCode: answer.Status);
        }
    }

    private static bool Forwardable(string header) =>
        !header.StartsWith("x-dcms", StringComparison.OrdinalIgnoreCase)
        && !header.StartsWith("x-forwarded", StringComparison.OrdinalIgnoreCase)
        && !header.Equals("x-real-ip", StringComparison.OrdinalIgnoreCase)
        && !header.Equals(ApiKeyService.Header, StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]?> ReadBoundedAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
