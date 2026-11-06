using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Dcms.Cli;

/// <summary>
/// Sign-in (the OAuth device flow against identity, ADR 0031) and every call to the console's
/// API as the signed-in person. The server decides everything: this only carries the person's
/// bearer, the link's workspace and environment, and an idempotency key on writes.
/// </summary>
internal sealed class Api(Config config, HttpClient http, TextWriter output)
{
    public const string ClientId = "dcms-cli";
    public const string Scopes = "openid offline_access profile email roles dcms.admin";
    public const string DefaultServer = "https://admin.highgeek.eu";

    /// <summary>Where sign-in lives: identity's own host, <c>auth.</c> beside the console's <c>admin.</c>.</summary>
    public static string AuthOrigin(string server)
    {
        var url = new UriBuilder(server) { Path = "", Query = "", Fragment = "" };
        if (url.Host.StartsWith("admin.", StringComparison.Ordinal)) url.Host = "auth." + url.Host["admin.".Length..];
        return Origin(url.Uri);
    }

    public static string Origin(Uri uri) => uri.GetLeftPart(UriPartial.Authority);

    /// <summary>
    /// A console or identity address: https, or plain http to this machine only (local development).
    /// The sign-in, every call and <c>dcms update</c>'s download go here; over plain http anyone on
    /// the way could read the token or swap the binary and its SHA256SUMS together.
    /// </summary>
    public static string Origin(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new CliException($"{url} is not a web address.");
        if (uri.Scheme == "http" && !uri.IsLoopback)
            throw new CliException($"{url} is not https: dcms talks to a console over https only (plain http just to this machine).");
        return Origin(uri);
    }

    /// <summary>The tokens a token endpoint answered, as kept: a reply without a refresh token keeps the one we have.</summary>
    public static Credentials Kept(Credentials? before, string server, string tokenEndpoint, JsonNode tokens, long now)
    {
        var expiresIn = tokens["expires_in"] is JsonValue e ? e.GetValue<long>() : 600;
        return new Credentials(server, tokenEndpoint,
            (string?)tokens["access_token"] ?? throw new CliException("Identity answered without a token."),
            (string?)tokens["refresh_token"] ?? before?.RefreshToken,
            now + expiresIn * 1000);
    }

    public async Task LoginAsync(string? serverOption, string? authOption, CancellationToken ct)
    {
        var server = Origin(serverOption ?? config.ReadCredentials()?.Server ?? DefaultServer);
        var auth = authOption is null ? AuthOrigin(server) : Origin(authOption);
        var discovery = await JsonAsync(await http.GetAsync($"{auth}/.well-known/openid-configuration", ct), ct);
        var tokenEndpoint = Https((string)discovery!["token_endpoint"]!);
        var device = await JsonAsync(await http.PostAsync(Https((string)discovery["device_authorization_endpoint"]!),
            Form(("client_id", ClientId), ("scope", Scopes)), ct), ct);

        output.WriteLine($"Open {(string?)device!["verification_uri_complete"] ?? (string?)device["verification_uri"]} and approve the code {(string?)device["user_code"]}.");
        var interval = TimeSpan.FromSeconds(device["interval"] is JsonValue i ? i.GetValue<int>() : 5);
        var until = DateTime.UtcNow.AddSeconds(device["expires_in"] is JsonValue x ? x.GetValue<int>() : 600);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(interval, ct);
            using var res = await http.PostAsync(tokenEndpoint, Form(
                ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"), ("device_code", (string)device["device_code"]!), ("client_id", ClientId)), ct);
            var body = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
            if (res.IsSuccessStatusCode)
            {
                config.WriteCredentials(Kept(null, server, tokenEndpoint, body!, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                output.WriteLine($"Signed in to {server}.");
                return;
            }
            var error = (string?)body?["error"];
            if (error == "slow_down") interval += TimeSpan.FromSeconds(5);
            else if (error != "authorization_pending")
                throw new CliException(error == "access_denied" ? "The code was denied." : (string?)body?["error_description"] ?? error ?? $"{(int)res.StatusCode}");
        }
        throw new CliException("The code expired. Run `dcms login` again.");
    }

    /// <summary>A token good for at least another minute, refreshed under the machine-wide lock if not.</summary>
    public async Task<Credentials> FreshAsync(CancellationToken ct)
    {
        var creds = config.ReadCredentials() ?? throw new CliException("Not signed in. Run `dcms login`.");
        Origin(creds.Server);
        Origin(creds.TokenEndpoint);
        if (creds.ExpiresAt - 60_000 > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return creds;
        using (await config.LockAsync(ct))
        {
            // Another command may have refreshed while this one waited.
            creds = config.ReadCredentials() ?? throw new CliException("Not signed in. Run `dcms login`.");
            if (creds.ExpiresAt - 60_000 > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return creds;
            if (creds.RefreshToken is null) throw new CliException("The sign-in expired. Run `dcms login`.");
            using var res = await http.PostAsync(creds.TokenEndpoint, Form(
                ("grant_type", "refresh_token"), ("refresh_token", creds.RefreshToken), ("client_id", ClientId)), ct);
            if (!res.IsSuccessStatusCode) throw new CliException("The sign-in expired or was removed. Run `dcms login`.");
            var next = Kept(creds, creds.Server, creds.TokenEndpoint, JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            config.WriteCredentials(next);
            return next;
        }
    }

    /// <summary>The linked app's functions API: <c>/api/admin/plugins/{app}/_functions{path}</c>.</summary>
    public Task<JsonNode?> FunctionsAsync(Link link, string path, JsonNode? body = null, HttpMethod? method = null, CancellationToken ct = default) =>
        CallAsync($"/api/admin/plugins/{Uri.EscapeDataString(link.App)}/_functions{path}", link, body, method, ct);

    public async Task<JsonNode?> CallAsync(string path, Link? link, JsonNode? body, HttpMethod? method, CancellationToken ct)
    {
        using var res = await SendAsync(path, link, body, method, ct);
        return res.StatusCode == HttpStatusCode.NoContent ? null : await JsonAsync(res, ct);
    }

    public async Task<byte[]> BytesAsync(string path, Link? link, CancellationToken ct)
    {
        using var res = await SendAsync(path, link, null, HttpMethod.Get, ct);
        if (!res.IsSuccessStatusCode) await JsonAsync(res, ct);
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>An admin API call as the signed-in person: their bearer, the link's tenant and environment.</summary>
    private async Task<HttpResponseMessage> SendAsync(string path, Link? link, JsonNode? body, HttpMethod? method, CancellationToken ct)
    {
        var creds = await FreshAsync(ct);
        var verb = method ?? (body is null ? HttpMethod.Get : HttpMethod.Post);
        var req = new HttpRequestMessage(verb, creds.Server + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Add("X-Dcms-Request-Id", Guid.NewGuid().ToString("N"));
        if (link is not null)
        {
            req.Headers.Add("X-Dcms-Tenant", link.Tenant);
            req.Headers.Add("X-Dcms-Environment", link.Environment);
        }
        // Every write carries one, as the console's do (ADR 0024): a retried request is then one action.
        if (verb != HttpMethod.Get && verb != HttpMethod.Head) req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            res.Dispose();
            throw new CliException("Not signed in, or the sign-in was removed. Run `dcms login`.");
        }
        if (res.StatusCode == HttpStatusCode.Forbidden)
        {
            res.Dispose();
            throw new CliException($"You may not do that here ({path}).");
        }
        return res;
    }

    /// <summary>The JSON body of a response, or the server's own words for why it failed.</summary>
    public static async Task<JsonNode?> JsonAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonNode? body = null;
        try
        {
            body = text.Length > 0 ? JsonNode.Parse(text) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            // not JSON: the status says it
        }
        if (!res.IsSuccessStatusCode)
        {
            var said = Str(body?["error_description"]) ?? Str(body?["error"]) ?? Str(body?["title"]) ?? Str(body?["detail"]);
            throw new CliException(said ?? $"{(int)res.StatusCode} {res.ReasonPhrase}");
        }
        return body;
    }

    /// <summary>An endpoint identity's discovery named, held to <see cref="Origin(string)"/>'s rule; the path kept.</summary>
    private static string Https(string url)
    {
        Origin(url);
        return url;
    }

    /// <summary>A JSON string's value, or null for anything else (an object where a message was expected, say).</summary>
    public static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
}
