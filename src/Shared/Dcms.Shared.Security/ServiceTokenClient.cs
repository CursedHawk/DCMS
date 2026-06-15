using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Dcms.Shared.Security;

/// <summary>
/// Acquires and caches client-credentials access tokens for service-to-service
/// calls. Refreshes shortly before expiry. Registered as a singleton with a
/// named HttpClient pointing at the identity token endpoint.
/// </summary>
public interface IServiceTokenProvider
{
    Task<string> GetTokenAsync(string scope, CancellationToken ct = default);
}

public sealed class ServiceTokenClient(HttpClient httpClient, IOptions<ServiceClientOptions> options)
    : IServiceTokenProvider
{
    private readonly ServiceClientOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CachedToken> _cache = new(StringComparer.Ordinal);

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

        using var response = await httpClient.SendAsync(request, ct);
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
