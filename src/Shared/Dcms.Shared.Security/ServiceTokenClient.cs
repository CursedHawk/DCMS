using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dcms.Shared.Security;

/// <summary>
/// Acquires and caches client-credentials access tokens for service-to-service
/// calls. Refreshes shortly before expiry.
///
/// <para><b>Must be a singleton</b> — register it with <see cref="ServiceTokenClientExtensions.AddServiceTokenProvider"/>.
/// It used to be registered as a typed HttpClient, which is transient: every resolve got an empty
/// cache, so every proxied console call and every job tick minted a fresh token, and identity's
/// audit log filled with <c>auth.token.issued</c>. The HttpClient is taken from the factory per
/// request so the singleton still follows identity to a new address after a redeploy.</para>
/// </summary>
public interface IServiceTokenProvider
{
    Task<string> GetTokenAsync(string scope, CancellationToken ct = default);
}

public sealed class ServiceTokenClient(
    IHttpClientFactory httpClientFactory,
    IOptions<ServiceClientOptions> options,
    string httpClientName = ServiceTokenClient.HttpClientName)
    : IServiceTokenProvider
{
    public const string HttpClientName = "dcms-service-tokens";

    private readonly ServiceClientOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Read outside the gate, so it has to be safe against the write inside it.
    private readonly ConcurrentDictionary<string, CachedToken> _cache = new(StringComparer.Ordinal);

    public async Task<string> GetTokenAsync(string scope, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(scope, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30))
        {
            return cached.AccessToken;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(scope, out cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30))
            {
                return cached.AccessToken;
            }

            var token = await RequestTokenAsync(scope, ct);
            _cache[scope] = token;
            return token.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CachedToken> RequestTokenAsync(string scope, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["scope"] = scope,
            }),
        };

        using var response = await httpClientFactory.CreateClient(httpClientName).SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(ct)
                      ?? throw new InvalidOperationException("Empty token response.");

        return new CachedToken(payload.AccessToken, DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn));
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);

    private sealed record TokenResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string AccessToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("expires_in")] int ExpiresIn);
}

public static class ServiceTokenClientExtensions
{
    /// <summary>The service's own <see cref="IServiceTokenProvider"/>, on <c>ServiceClient:*</c>. One per process.</summary>
    public static IServiceCollection AddServiceTokenProvider(this IServiceCollection services)
    {
        services.AddHttpClient(ServiceTokenClient.HttpClientName);
        services.AddSingleton<IServiceTokenProvider, ServiceTokenClient>();
        return services;
    }
}
