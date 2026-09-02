using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Dcms.Shared.Data.Social;
using Microsoft.Extensions.Options;

namespace Dcms.AdminApi.Social;

/// <summary>
/// The OAuth half of the Meta integration: build the consent URL, exchange the code, and
/// upgrade or refresh a long-lived token. Reading content is <c>MetaGraphClient</c>'s job.
///
/// <para>The two login paths differ in host, in the shape of the long-lived exchange, and in
/// what a refresh even means, so each branch is written out rather than hidden behind a
/// parameter — the differences are the interesting part.</para>
/// </summary>
public sealed class MetaOAuthClient(
    HttpClient http, IOptions<MetaSocialOptions> options, ILogger<MetaOAuthClient> logger)
{
    private readonly MetaSocialOptions _opts = options.Value;

    // Every Meta host funnels through these two properties so a test can redirect all of them
    // at once via MetaSocialOptions.OverrideBaseUrl. The paths below are distinct across hosts,
    // so collapsing them onto one stub origin stays unambiguous.
    private string Root => _opts.OverrideBaseUrl?.TrimEnd('/') ?? "https://graph.facebook.com";
    private string FacebookHost => $"{Root}/{_opts.GraphVersion}";
    private string InstagramHost => _opts.OverrideBaseUrl?.TrimEnd('/') ?? "https://graph.instagram.com";
    private string InstagramApiHost => _opts.OverrideBaseUrl?.TrimEnd('/') ?? "https://api.instagram.com";
    private string DialogHost => _opts.OverrideBaseUrl?.TrimEnd('/') ?? "https://www.facebook.com";
    private string InstagramAuthorizeHost => _opts.OverrideBaseUrl?.TrimEnd('/') ?? "https://www.instagram.com";

    /// <summary>The URL to send the admin's browser to for consent.</summary>
    public string BuildAuthorizeUrl(MetaProvider provider, string state)
    {
        var app = _opts.App(provider);
        var scopes = string.Join(",", MetaScopes.For(provider));
        var redirect = Uri.EscapeDataString(_opts.RedirectUri!);

        return provider == MetaProvider.Facebook
            ? $"{DialogHost}/{_opts.GraphVersion}/dialog/oauth" +
              $"?client_id={app.AppId}&redirect_uri={redirect}" +
              $"&state={Uri.EscapeDataString(state)}&response_type=code&scope={Uri.EscapeDataString(scopes)}"
            : $"{InstagramAuthorizeHost}/oauth/authorize" +
              $"?client_id={app.AppId}&redirect_uri={redirect}" +
              $"&state={Uri.EscapeDataString(state)}&response_type=code&scope={Uri.EscapeDataString(scopes)}";
    }

    /// <summary>
    /// Exchange the authorization code for a long-lived token. Both paths need two calls —
    /// Meta only ever issues a short-lived token from a code — so this does both and returns
    /// the durable one, because a caller that forgot the second step would store a token that
    /// dies in an hour and look fine until it did.
    /// </summary>
    public async Task<MetaToken> ExchangeCodeAsync(MetaProvider provider, string code, CancellationToken ct)
    {
        var app = _opts.App(provider);

        if (provider == MetaProvider.Facebook)
        {
            var shortLived = await GetAsync<TokenResponse>(
                $"{FacebookHost}/oauth/access_token" +
                $"?client_id={app.AppId}&client_secret={app.AppSecret}" +
                $"&redirect_uri={Uri.EscapeDataString(_opts.RedirectUri!)}&code={Uri.EscapeDataString(code)}", ct);

            var longLived = await GetAsync<TokenResponse>(
                $"{FacebookHost}/oauth/access_token" +
                $"?grant_type=fb_exchange_token&client_id={app.AppId}&client_secret={app.AppSecret}" +
                $"&fb_exchange_token={Uri.EscapeDataString(shortLived.AccessToken)}", ct);

            return MetaToken.From(longLived);
        }

        // Instagram Login: the code exchange is a form POST, and the long-lived upgrade is a
        // different endpoint on a different host from Facebook's.
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = app.AppId!,
            ["client_secret"] = app.AppSecret!,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = _opts.RedirectUri!,
            ["code"] = code,
        });
        using var res = await http.PostAsync($"{InstagramApiHost}/oauth/access_token", form, ct);
        if (!res.IsSuccessStatusCode) await ThrowFor(res, ct);
        var shortToken = await res.Content.ReadFromJsonAsync<InstagramShortTokenResponse>(ct)
                         ?? throw new MetaApiException(res.StatusCode, "Empty token response.");

        var exchanged = await GetAsync<TokenResponse>(
            $"{InstagramHost}/access_token?grant_type=ig_exchange_token" +
            $"&client_secret={app.AppSecret}&access_token={Uri.EscapeDataString(shortToken.AccessToken)}", ct);

        return MetaToken.From(exchanged);
    }

    /// <summary>Extend a long-lived token that is nearing expiry.</summary>
    public async Task<MetaToken> RefreshAsync(MetaProvider provider, string accessToken, CancellationToken ct)
    {
        var app = _opts.App(provider);

        if (provider == MetaProvider.Facebook)
        {
            var refreshed = await GetAsync<TokenResponse>(
                $"{FacebookHost}/oauth/access_token" +
                $"?grant_type=fb_exchange_token&client_id={app.AppId}&client_secret={app.AppSecret}" +
                $"&fb_exchange_token={Uri.EscapeDataString(accessToken)}", ct);
            return MetaToken.From(refreshed);
        }

        var result = await GetAsync<TokenResponse>(
            $"{InstagramHost}/refresh_access_token?grant_type=ig_refresh_token" +
            $"&access_token={Uri.EscapeDataString(accessToken)}", ct);
        return MetaToken.From(result);
    }

    /// <summary>
    /// Ask Meta who we just connected as. One consent can yield several accounts — a
    /// Facebook user may administer many Pages, and each Page may have an Instagram account
    /// linked — so this returns a list and the caller stores one connection per entry.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredAccount>> DiscoverAccountsAsync(
        MetaProvider provider, string accessToken, CancellationToken ct)
    {
        if (provider == MetaProvider.InstagramLogin)
        {
            var me = await GetAsync<InstagramMe>(
                $"{InstagramHost}/me?fields=id,username,name,profile_picture_url" +
                $"&access_token={Uri.EscapeDataString(accessToken)}", ct);

            return
            [
                new DiscoveredAccount(
                    MetaProvider.InstagramLogin, me.Id, PageId: null,
                    Name: me.Name ?? me.Username ?? me.Id, Username: me.Username,
                    AvatarUrl: me.ProfilePictureUrl, PageToken: null),
            ];
        }

        var pages = await GetAsync<PagesEnvelope>(
            $"{FacebookHost}/me/accounts" +
            $"?fields=id,name,access_token,picture{{url}},instagram_business_account{{id,username,name,profile_picture_url}}" +
            $"&access_token={Uri.EscapeDataString(accessToken)}", ct);

        var accounts = new List<DiscoveredAccount>();
        foreach (var page in pages.Data ?? [])
        {
            accounts.Add(new DiscoveredAccount(
                MetaProvider.Facebook, page.Id, PageId: page.Id,
                Name: page.Name ?? page.Id, Username: null,
                AvatarUrl: page.Picture?.Data?.Url, PageToken: page.AccessToken));

            // The linked Instagram account is reached with the *Page's* token, not the user's.
            // Carrying the Page id on the row is what later lets the sync and the stories
            // endpoint pick the right credential without re-walking /me/accounts.
            if (page.InstagramBusinessAccount is { Id: { Length: > 0 } igId } ig)
            {
                accounts.Add(new DiscoveredAccount(
                    MetaProvider.Facebook, igId, PageId: page.Id,
                    Name: ig.Name ?? ig.Username ?? igId, Username: ig.Username,
                    AvatarUrl: ig.ProfilePictureUrl, PageToken: page.AccessToken));
            }
        }
        return accounts;
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var res = await http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) await ThrowFor(res, ct);
        return await res.Content.ReadFromJsonAsync<T>(ct)
               ?? throw new MetaApiException(res.StatusCode, "Empty response body.");
    }

    private async Task ThrowFor(HttpResponseMessage res, CancellationToken ct)
    {
        var body = await res.Content.ReadAsStringAsync(ct);
        // The URL carries the app secret and the token as query parameters, so it must never
        // be logged. Method and status are enough to place the failure.
        logger.LogError("Meta OAuth {Method} -> {Status}: {Body}",
            res.RequestMessage?.Method, (int)res.StatusCode, body);
        throw new MetaApiException(res.StatusCode, body);
    }

    /// <summary>
    /// One account a consent granted access to. <see cref="PageId"/> is set for anything
    /// reached through a Facebook Page — including the Instagram account linked to it, which
    /// is why an Instagram row can still carry a Page id and a Page token.
    /// </summary>
    public sealed record DiscoveredAccount(
        MetaProvider Provider,
        string ExternalAccountId,
        string? PageId,
        string Name,
        string? Username,
        string? AvatarUrl,
        string? PageToken)
    {
        /// <summary>True when this row is an Instagram account rather than the Page itself.</summary>
        public bool IsInstagram => Provider == MetaProvider.InstagramLogin || PageId != ExternalAccountId;
    }

    private sealed record InstagramMe(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("profile_picture_url")] string? ProfilePictureUrl);

    private sealed record PagesEnvelope(
        [property: JsonPropertyName("data")] List<PageNode>? Data);

    private sealed record PageNode(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("picture")] PictureEnvelope? Picture,
        [property: JsonPropertyName("instagram_business_account")] InstagramMe? InstagramBusinessAccount);

    private sealed record PictureEnvelope(
        [property: JsonPropertyName("data")] PictureData? Data);

    private sealed record PictureData(
        [property: JsonPropertyName("url")] string? Url);

    internal sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("expires_in")] long? ExpiresIn);

    private sealed record InstagramShortTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("user_id")] string? UserId);

    /// <summary>A token plus when it dies. Null expiry means Meta did not tell us.</summary>
    public sealed record MetaToken(string AccessToken, DateTimeOffset? ExpiresAt)
    {
        internal static MetaToken From(TokenResponse r) => new(
            r.AccessToken,
            r.ExpiresIn is > 0 ? DateTimeOffset.UtcNow.AddSeconds(r.ExpiresIn.Value) : null);
    }
}

public sealed class MetaApiException(HttpStatusCode status, string body)
    : Exception($"Meta API returned {(int)status}: {body}")
{
    public HttpStatusCode Status { get; } = status;
    public string Body { get; } = body;

    /// <summary>
    /// Meta code 190 means the token is dead — revoked, password changed, or expired.
    /// It is the signal to stop retrying and ask the admin to reconnect, so it is worth
    /// distinguishing from a transient 5xx that backoff should simply ride out.
    /// </summary>
    public bool IsTokenInvalid =>
        Status == HttpStatusCode.Unauthorized || Body.Contains("\"code\":190");
}
