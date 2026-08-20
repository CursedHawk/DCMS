using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dcms.Identity.Forgejo;

/// <summary>
/// Thin typed client over the Forgejo (Gitea-compatible) admin REST API v1, scoped
/// to the operations Identity needs to mirror DCMS accounts: create a user, update
/// its login credentials (email/password), find it by email, and register SSH keys.
/// Auth is the admin machine token sent as "Authorization: token &lt;AdminToken&gt;"
/// (must carry the <c>write:admin</c> scope). JSON is snake_case, matching the API.
/// </summary>
public sealed class ForgejoAdminClient(HttpClient http, ILogger<ForgejoAdminClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Create a Forgejo user. Returns the created user, or null if the
    /// username/email already exists (422) — callers then fall back to a lookup.</summary>
    public async Task<ForgejoUser?> CreateUserAsync(
        string username, string email, string? password, CancellationToken ct)
    {
        var body = new CreateUserOption(
            Username: username,
            Email: email,
            Password: password,
            // A user with no password (Google-only) still gets a valid account; they
            // authenticate git via SSH key or by setting a password later.
            MustChangePassword: false);
        using var res = await http.PostAsJsonAsync("/api/v1/admin/users", body, Json, ct);
        if (res.StatusCode == HttpStatusCode.Created)
            return await res.Content.ReadFromJsonAsync<ForgejoUser>(Json, ct);
        if (res.StatusCode == HttpStatusCode.UnprocessableEntity)
            return null; // already exists (username or email taken)
        await ThrowFor(res, ct);
        return null;
    }

    /// <summary>Update an existing user's login credentials. <paramref name="password"/>
    /// null leaves the password unchanged; <paramref name="email"/> null leaves the email.
    /// <c>source_id</c> + <c>login_name</c> are required together by the API (omitting them
    /// yields 422).</summary>
    public async Task SetCredentialsAsync(
        string username, string? email, string? password, CancellationToken ct)
    {
        var body = new EditUserOption(
            SourceId: 0,
            LoginName: username,
            Email: email,
            Password: password);
        using var res = await http.PatchAsJsonAsync(
            $"/api/v1/admin/users/{Uri.EscapeDataString(username)}", body, Json, ct);
        if (res.StatusCode != HttpStatusCode.OK) await ThrowFor(res, ct);
    }

    /// <summary>Find a user by exact email (used to reconcile when a create raced or the
    /// local mapping was lost). Returns null if none matches.</summary>
    public async Task<ForgejoUser?> FindByEmailAsync(string email, CancellationToken ct)
    {
        var res = await http.GetFromJsonAsync<SearchResult>(
            $"/api/v1/users/search?q={Uri.EscapeDataString(email)}&limit=50", Json, ct);
        return res?.Data?.FirstOrDefault(u =>
            string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Register an SSH public key for a user (admin API). Returns the created
    /// key. A 422 (malformed or already-registered key) throws so the caller can surface it.</summary>
    public async Task<ForgejoKey> AddPublicKeyAsync(string username, string title, string key, CancellationToken ct)
    {
        var body = new CreateKeyOption(title, key, ReadOnly: false);
        using var res = await http.PostAsJsonAsync(
            $"/api/v1/admin/users/{Uri.EscapeDataString(username)}/keys", body, Json, ct);
        if (res.StatusCode != HttpStatusCode.Created) await ThrowFor(res, ct);
        return (await res.Content.ReadFromJsonAsync<ForgejoKey>(Json, ct))!;
    }

    /// <summary>List a user's registered SSH public keys.</summary>
    public async Task<IReadOnlyList<ForgejoKey>> ListPublicKeysAsync(string username, CancellationToken ct)
    {
        var list = await http.GetFromJsonAsync<List<ForgejoKey>>(
            $"/api/v1/users/{Uri.EscapeDataString(username)}/keys?limit=100", Json, ct);
        return list ?? [];
    }

    /// <summary>Delete one of a user's SSH public keys (admin API). A 404 is treated as success.</summary>
    public async Task DeletePublicKeyAsync(string username, long id, CancellationToken ct)
    {
        using var res = await http.DeleteAsync(
            $"/api/v1/admin/users/{Uri.EscapeDataString(username)}/keys/{id}", ct);
        if (res.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.NotFound))
            await ThrowFor(res, ct);
    }

    /// <summary>
    /// Delete a mirrored user account (admin API). A 404 counts as success so a
    /// re-run of a partly-finished account deletion still completes. <c>purge</c>
    /// removes the repos and org memberships the user still owns — without it
    /// Forgejo refuses to delete an account that owns anything.
    /// </summary>
    public async Task DeleteUserAsync(string username, CancellationToken ct)
    {
        using var res = await http.DeleteAsync(
            $"/api/v1/admin/users/{Uri.EscapeDataString(username)}?purge=true", ct);
        if (res.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.NotFound))
            await ThrowFor(res, ct);
    }

    private async Task ThrowFor(HttpResponseMessage res, CancellationToken ct)
    {
        var body = await res.Content.ReadAsStringAsync(ct);
        logger.LogError("Forgejo admin API {Method} {Uri} -> {Status}: {Body}",
            res.RequestMessage?.Method, res.RequestMessage?.RequestUri, (int)res.StatusCode, body);
        throw new ForgejoApiException(res.StatusCode, body);
    }

    // ---------- DTOs ----------

    private sealed record CreateUserOption(
        string Username, string Email, string? Password,
        [property: JsonPropertyName("must_change_password")] bool MustChangePassword);

    private sealed record EditUserOption(
        [property: JsonPropertyName("source_id")] long SourceId,
        [property: JsonPropertyName("login_name")] string LoginName,
        string? Email, string? Password);

    private sealed record CreateKeyOption(string Title, string Key, bool ReadOnly);

    private sealed record SearchResult(List<ForgejoUser>? Data, bool Ok);
}

/// <summary>A Forgejo user as returned by the API (only the fields we use).</summary>
public sealed record ForgejoUser(long Id, string Login, string Email);

/// <summary>A Forgejo SSH public key (subset of fields shown in the account UI).</summary>
public sealed record ForgejoKey(long Id, string Title, string? Fingerprint, DateTimeOffset CreatedAt);

public sealed class ForgejoApiException(HttpStatusCode status, string body)
    : Exception($"Forgejo admin API error {(int)status}: {body}")
{
    public HttpStatusCode Status { get; } = status;
}
