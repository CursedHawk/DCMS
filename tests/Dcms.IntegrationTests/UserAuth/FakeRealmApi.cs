using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.Shared.Contracts.Realms;

namespace Dcms.IntegrationTests.UserAuth;

/// <summary>
/// identity's realm admin API, in memory, for the admin-api fixture — which has no identity.
/// Answers with the same records identity does (Dcms.Shared.Contracts.Realms), so what admin-api
/// sends and reads is checked against the real shapes; identity's own behaviour behind those
/// shapes is RealmTests' business. Only what the User Authentication tests use.
/// </summary>
public sealed class FakeRealmApi : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, Realm> _realms = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Every call, as "METHOD /path", for tests that care what admin-api asked.</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    public RealmInfo? RealmOf(Guid tenantId) => _realms.TryGetValue(tenantId, out var realm) ? realm.Info : null;

    private sealed class Realm(Guid tenantId)
    {
        public RealmInfo Info { get; set; } = new(tenantId, "", "", [], $"site:{tenantId:N}", true, true);
        public Dictionary<Guid, RealmUserInfo> Users { get; } = [];
        public Dictionary<Guid, RealmGroupInfo> Groups { get; } = [];
        public HashSet<(Guid Group, Guid User)> Members { get; } = [];
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // /api/realms/{tenantId}{rest}
        var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var tenantId = Guid.Parse(segments[2]);
        var rest = segments.Skip(3).ToArray();
        Calls.Enqueue($"{request.Method} /{string.Join('/', rest)}");
        await _gate.WaitAsync(ct);
        try
        {
            return await Handle(request, tenantId, _realms.GetValueOrDefault(tenantId), rest, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HttpResponseMessage> Handle(HttpRequestMessage request, Guid tenantId, Realm? realm, string[] rest, CancellationToken ct)
    {
        var method = request.Method.Method;
        if (rest.Length == 0)
        {
            switch (method)
            {
                case "GET":
                    return realm is null ? Status(HttpStatusCode.NotFound) : Ok(realm.Info);
                case "PUT":
                    var body = (await Body<RealmUpsert>(request, ct))!;
                    realm = _realms.GetOrAdd(tenantId, id => new Realm(id));
                    realm.Info = realm.Info with
                    {
                        Slug = body.Slug, Name = body.Name, Hosts = body.Hosts ?? [],
                        PasswordEnabled = body.PasswordEnabled ?? realm.Info.PasswordEnabled,
                    };
                    return Ok(realm.Info);
                case "DELETE":
                    return _realms.TryRemove(tenantId, out _) ? Status(HttpStatusCode.NoContent) : Status(HttpStatusCode.NotFound);
            }
        }
        if (realm is null)
        {
            return Status(HttpStatusCode.NotFound);
        }
        switch (method, rest)
        {
            case ("GET", ["users"]):
                var users = realm.Users.Values.Select(u => WithGroups(realm, u)).OrderBy(u => u.Email).ToList();
                return Ok(new RealmUserPage(users, users.Count, 1, 50));
            case ("POST", ["users", "invite"]):
                var invite = (await Body<RealmInvite>(request, ct))!;
                if (!invite.Email.Contains('@'))
                {
                    return Error(HttpStatusCode.BadRequest, "Enter a valid email address.");
                }
                if (realm.Users.Values.Any(u => u.Email == invite.Email))
                {
                    return Error(HttpStatusCode.Conflict, "That email already has an account here.");
                }
                var user = new RealmUserInfo(Guid.NewGuid(), invite.Email, invite.DisplayName, "invited", [], false, false, DateTimeOffset.UtcNow, null);
                realm.Users[user.Id] = user;
                foreach (var group in invite.Groups ?? [])
                {
                    realm.Members.Add((group, user.Id));
                }
                return Ok(new RealmInviteResult(WithGroups(realm, user), "https://identity.test/realm/invite?token=x"));
            case ("PATCH", ["users", var id]) when realm.Users.TryGetValue(Guid.Parse(id), out var existing):
                var patch = (await Body<RealmUserPatch>(request, ct))!;
                realm.Users[existing.Id] = existing = existing with
                {
                    DisplayName = patch.DisplayName ?? existing.DisplayName, Status = patch.Status ?? existing.Status,
                };
                return Ok(WithGroups(realm, existing));
            case ("DELETE", ["users", var id]):
                return realm.Users.Remove(Guid.Parse(id)) ? Status(HttpStatusCode.NoContent) : Status(HttpStatusCode.NotFound);
            case ("GET", ["groups"]):
                return Ok(realm.Groups.Values.Select(g => g with { Members = realm.Members.Count(m => m.Group == g.Id) }).ToList());
            case ("POST", ["groups"]):
                var write = (await Body<RealmGroupWrite>(request, ct))!;
                var created = new RealmGroupInfo(Guid.NewGuid(), write.Name, write.Description, 0);
                realm.Groups[created.Id] = created;
                return Ok(created, HttpStatusCode.Created);
            case ("DELETE", ["groups", var id]):
                return realm.Groups.Remove(Guid.Parse(id)) ? Status(HttpStatusCode.NoContent) : Status(HttpStatusCode.NotFound);
            case ("PUT", ["groups", var group, "members", var member]):
                realm.Members.Add((Guid.Parse(group), Guid.Parse(member)));
                return Status(HttpStatusCode.NoContent);
            case ("DELETE", ["groups", var group, "members", var member]):
                realm.Members.Remove((Guid.Parse(group), Guid.Parse(member)));
                return Status(HttpStatusCode.NoContent);
            case ("GET", ["providers"]):
                return Ok(Array.Empty<RealmProviderInfo>());
            default:
                return Status(HttpStatusCode.NotFound);
        }
    }

    private static RealmUserInfo WithGroups(Realm realm, RealmUserInfo user) =>
        user with { Groups = realm.Members.Where(m => m.User == user.Id).Select(m => m.Group).ToList() };

    private static async Task<T?> Body<T>(HttpRequestMessage request, CancellationToken ct) =>
        request.Content is null ? default : await request.Content.ReadFromJsonAsync<T>(Json, ct);

    private static HttpResponseMessage Ok(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(body, body.GetType(), options: Json) };

    private static HttpResponseMessage Error(HttpStatusCode status, string error) => Ok(new { error }, status);

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status);
}
