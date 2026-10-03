using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Connections;

/// <summary>
/// Calls a connection's operations and stores each good response as its snapshot. The one place
/// a connection's credential is ever decrypted.
///
/// <para>An operation that fails keeps its previous snapshot — a provider's bad minute does not
/// empty a site — and the failure is recorded on the connection for the console.</para>
/// </summary>
public sealed class ApiConnectionFetcher(
    CmsDbContext db, ITransitEncryptor transit, IHttpClientFactory http, ILogger<ApiConnectionFetcher> logger)
{
    public const string HttpClientName = "api-connections";

    public async Task RefreshAsync(ApiConnection connection, CancellationToken ct)
    {
        var secret = connection.SecretCiphertext is null
            ? null
            : Encoding.UTF8.GetString(await transit.DecryptAsync(ApiConnectionRules.TransitKey, connection.SecretCiphertext, ct));

        var existing = await db.ApiSnapshots.Where(s => s.ConnectionId == connection.Id).ToListAsync(ct);
        // Operations no longer allowed are no longer served.
        db.ApiSnapshots.RemoveRange(existing.Where(s => !connection.Operations.Contains(s.Operation)));

        var errors = new List<string>();
        var client = http.CreateClient(HttpClientName);
        foreach (var operation in connection.Operations)
        {
            try
            {
                var body = await FetchAsync(client, connection, operation, secret, ct);
                var (itemsPath, fields) = Shape(body);
                var row = existing.FirstOrDefault(s => s.Operation == operation);
                if (row is null)
                {
                    db.ApiSnapshots.Add(new ApiSnapshot
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = connection.TenantId,
                        ConnectionId = connection.Id,
                        Operation = operation,
                        Body = body,
                        ItemsPath = itemsPath,
                        Fields = fields,
                    });
                }
                else
                {
                    row.Body = body;
                    row.ItemsPath = itemsPath;
                    row.Fields = fields;
                    row.FetchedAt = DateTimeOffset.UtcNow;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or InvalidOperationException or JsonException)
            {
                // The message only: never the request, which carries the credential.
                errors.Add($"{operation}: {ex.Message}");
                logger.LogInformation("API connection {Slug} {Operation} failed: {Error}", connection.Slug, operation, ex.Message);
            }
        }

        connection.RefreshedAt = DateTimeOffset.UtcNow;
        connection.LastError = errors.Count == 0 ? null : Truncate(string.Join("\n", errors), 2000);
        await db.SaveChangesAsync(ct);
    }

    private static async Task<string> FetchAsync(HttpClient client, ApiConnection connection, string operation, string? secret, CancellationToken ct)
    {
        var target = ApiConnectionRules.Target(connection.BaseUrl, operation);
        if (connection.AuthKind == "query" && secret is not null)
        {
            var separator = string.IsNullOrEmpty(target.Query) ? "?" : "&";
            target = new Uri($"{target.AbsoluteUri}{separator}{Uri.EscapeDataString(connection.AuthName!)}={Uri.EscapeDataString(secret)}");
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (secret is not null)
        {
            if (connection.AuthKind == "bearer") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            else if (connection.AuthKind == "header") request.Headers.TryAddWithoutValidation(connection.AuthName!, secret);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidDataException($"the API answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"the API answered {mediaType}, not JSON.");
        }
        if (response.Content.Headers.ContentLength > ApiConnectionRules.MaxBodyBytes)
        {
            throw new InvalidDataException($"the response is larger than {ApiConnectionRules.MaxBodyBytes / 1024} KB.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > ApiConnectionRules.MaxBodyBytes)
            {
                throw new InvalidDataException($"the response is larger than {ApiConnectionRules.MaxBodyBytes / 1024} KB.");
            }
            buffer.Write(chunk, 0, read);
        }
        // Parsed and re-serialised: what is stored (and served) is JSON, whatever was sent.
        using var json = JsonDocument.Parse(buffer.ToArray());
        return json.RootElement.GetRawText();
    }

    /// <summary>
    /// Where a response keeps its list, and what an item in it has: the response itself if it is
    /// an array of objects, else the first such array within two levels. Fields are the first
    /// item's properties, one level of nesting deep (<c>venue.city</c>), at most fifty.
    /// </summary>
    public static (string? ItemsPath, List<string> Fields) Shape(string body)
    {
        using var json = JsonDocument.Parse(body);
        var found = FindList(json.RootElement, null, depth: 0);
        if (found is not { } list)
        {
            return (null, []);
        }
        var fields = new List<string>();
        foreach (var property in list.First.EnumerateObject())
        {
            if (fields.Count >= 50) break;
            fields.Add(property.Name);
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                fields.AddRange(property.Value.EnumerateObject().Take(10).Select(p => $"{property.Name}.{p.Name}"));
            }
        }
        return (list.Path, fields.Take(50).ToList());
    }

    private static (string? Path, JsonElement First)? FindList(JsonElement element, string? path, int depth)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            var first = element.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
            return first.ValueKind == JsonValueKind.Object ? (path, first) : null;
        }
        if (element.ValueKind != JsonValueKind.Object || depth >= 2)
        {
            return null;
        }
        foreach (var property in element.EnumerateObject())
        {
            if (FindList(property.Value, path is null ? property.Name : $"{path}.{property.Name}", depth + 1) is { } hit)
            {
                return hit;
            }
        }
        return null;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
