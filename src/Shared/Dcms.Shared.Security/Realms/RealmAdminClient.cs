using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.Shared.Contracts.Realms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dcms.Shared.Security.Realms;

/// <summary>Identity refused a realm admin call; <see cref="Status"/> and its own message are passed on as they are.</summary>
public sealed class RealmAdminException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Identity's realm admin API (ADR 0022), as admin-api calls it: a client-credentials token with
/// the <c>dcms.realms</c> scope, every call naming the one tenant it acts on. The caller checks
/// the member's own permission first — identity trusts admin-api to have done so.
/// </summary>
public sealed class RealmAdminClient(HttpClient http, IServiceTokenProvider tokens)
{
    /// <summary>The one client identity gives <see cref="Scope"/>: not the service client admin-api shares with content-api.</summary>
    public const string ClientId = "dcms-realm-admin";

    public const string Scope = "dcms.realms";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The raw call, for routes that hand identity's answer straight back to the console.</summary>
    public async Task<HttpResponseMessage> SendAsync(Guid tenantId, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, $"api/realms/{tenantId}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(Scope, ct));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }
        return await http.SendAsync(request, ct);
    }

    /// <summary>A read; null when identity has no such thing (404).</summary>
    public async Task<T?> GetAsync<T>(Guid tenantId, string path, CancellationToken ct) where T : class
    {
        using var response = await SendAsync(tenantId, HttpMethod.Get, path, null, ct);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await ReadAsync<T>(response, ct);
    }

    public async Task<T> CallAsync<T>(Guid tenantId, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendAsync(tenantId, method, path, body, ct);
        return await ReadAsync<T>(response, ct);
    }

    public async Task CallAsync(Guid tenantId, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendAsync(tenantId, method, path, body, ct);
        await EnsureAsync(response, ct);
    }

    public Task<RealmInfo?> GetRealmAsync(Guid tenantId, CancellationToken ct) => GetAsync<RealmInfo>(tenantId, "", ct);

    public Task<RealmInfo> UpsertRealmAsync(Guid tenantId, RealmUpsert realm, CancellationToken ct) =>
        CallAsync<RealmInfo>(tenantId, HttpMethod.Put, "", realm, ct);

    /// <summary>Users, groups, logins, the client and its tokens. True when there was a realm to delete.</summary>
    public async Task<bool> DeleteRealmAsync(Guid tenantId, CancellationToken ct)
    {
        using var response = await SendAsync(tenantId, HttpMethod.Delete, "", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        await EnsureAsync(response, ct);
        return true;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new RealmAdminException(502, "Identity returned an empty answer.");
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var message = "Identity refused the request.";
        try
        {
            if (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct) is { ValueKind: JsonValueKind.Object } body
                && body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                message = error.GetString()!;
            }
        }
        catch (JsonException)
        {
        }
        // A 5xx or an auth failure at identity is ours, not the caller's: report it as a bad gateway.
        var status = (int)response.StatusCode is >= 400 and < 500 and not 401 and not 403 ? (int)response.StatusCode : 502;
        throw new RealmAdminException(status, message);
    }
}

public static class RealmAdminClientExtensions
{
    /// <summary>
    /// Registers <see cref="RealmAdminClient"/> against identity's internal address — the host of
    /// <c>ServiceClient:TokenEndpoint</c>, where this service already gets its tokens — with its
    /// own credentials, <c>Realms:ClientSecret</c> for <see cref="RealmAdminClient.ClientId"/>
    /// (Vault, seeded with identity's copy by <c>infra/vault/apply.sh --seed</c>).
    /// </summary>
    /// <param name="development">Only then is a missing secret replaced by the published dev one; elsewhere it stays empty and identity says no.</param>
    public static IServiceCollection AddRealmAdminClient(this IServiceCollection services, IConfiguration configuration, bool development)
    {
        var tokenEndpoint = configuration.GetSection(ServiceClientOptions.SectionName).Get<ServiceClientOptions>()?.TokenEndpoint
                            ?? new ServiceClientOptions().TokenEndpoint;
        var credentials = Options.Create(new ServiceClientOptions
        {
            TokenEndpoint = tokenEndpoint,
            ClientId = RealmAdminClient.ClientId,
            ClientSecret = configuration["Realms:ClientSecret"] is { Length: > 0 } secret ? secret
                : development ? "dcms-realm-admin-dev-secret" : string.Empty,
        });
        services.AddHttpClient(TokenClientName);
        // A token cache of its own: the shared IServiceTokenProvider holds admin-api's other client.
        services.AddSingleton(sp => new RealmAdminTokens(
            new ServiceTokenClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(TokenClientName), credentials)));
        services.AddHttpClient(nameof(RealmAdminClient), client => client.BaseAddress = new Uri(new Uri(tokenEndpoint), "/"))
            .AddTypedClient((http, sp) => new RealmAdminClient(http, sp.GetRequiredService<RealmAdminTokens>().Provider));
        return services;
    }

    private const string TokenClientName = "dcms-realm-admin-tokens";

    private sealed record RealmAdminTokens(IServiceTokenProvider Provider);
}
