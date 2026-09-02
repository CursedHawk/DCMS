extern alias AdminApiApp;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Npgsql;

namespace Dcms.IntegrationTests.Notifications;

/// <summary>
/// The bell's read plane. The property that matters is that a notification is only ever
/// visible to the user it was addressed to — the endpoints carry no permission check, so the
/// per-user predicate is the whole of the access control.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class NotificationEndpointTests(AdminApiFixture fixture)
{
    [DockerFact]
    public async Task A_user_sees_only_their_own_notifications()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var (tenantId, slug) = await CreateTenantAsync(client, ct);

        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var notificationId = await SeedNotificationAsync(tenantId, [mine, theirs], ct);

        var minePage = await ListAsync(client, slug, mine, ct);
        minePage.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid())
            .Should().Contain(notificationId);
        minePage.GetProperty("unreadCount").GetInt32().Should().Be(1);

        // A third user, addressed by nothing, sees an empty bell in the same tenant.
        var stranger = await ListAsync(client, slug, Guid.NewGuid(), ct);
        stranger.GetProperty("items").GetArrayLength().Should().Be(0);
        stranger.GetProperty("unreadCount").GetInt32().Should().Be(0);
    }

    [DockerFact]
    public async Task Fetching_someone_elses_notification_is_a_404_not_a_403()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var notificationId = await SeedNotificationAsync(tenantId, [Guid.NewGuid()], ct);

        var res = await client.SendAsync(
            Request(HttpMethod.Get, $"/api/admin/notifications/{notificationId}", slug, Guid.NewGuid()), ct);

        // 403 would confirm the notification exists, which is the reasoning GET /audit/{id}
        // already applies to another tenant's record.
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task Marking_read_clears_the_badge_for_that_user_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var notificationId = await SeedNotificationAsync(tenantId, [mine, theirs], ct);

        var read = await client.SendAsync(
            Request(HttpMethod.Post, $"/api/admin/notifications/{notificationId}/read", slug, mine), ct);
        read.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ListAsync(client, slug, mine, ct)).GetProperty("unreadCount").GetInt32().Should().Be(0);
        (await ListAsync(client, slug, theirs, ct)).GetProperty("unreadCount").GetInt32().Should().Be(1,
            "read state is per recipient, not per notification");
    }

    [DockerFact]
    public async Task Dismissing_removes_it_from_the_list_and_the_badge()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var mine = Guid.NewGuid();
        var notificationId = await SeedNotificationAsync(tenantId, [mine], ct);

        var res = await client.SendAsync(
            Request(HttpMethod.Post, $"/api/admin/notifications/{notificationId}/dismiss", slug, mine), ct);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var page = await ListAsync(client, slug, mine, ct);
        page.GetProperty("items").GetArrayLength().Should().Be(0);
        // Dismissing implies read: a dismissed row must never keep inflating the badge.
        page.GetProperty("unreadCount").GetInt32().Should().Be(0);
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string slug, Guid userId)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", userId.ToString());
        req.Headers.Add("X-Test-Roles", "SuperAdmin");
        req.Headers.Add("X-Dcms-Tenant", slug);
        return req;
    }

    private static async Task<JsonElement> ListAsync(
        HttpClient client, string slug, Guid userId, CancellationToken ct)
    {
        var res = await client.SendAsync(Request(HttpMethod.Get, "/api/admin/notifications", slug, userId), ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task<Guid> SeedNotificationAsync(Guid tenantId, Guid[] userIds, CancellationToken ct)
    {
        var notificationId = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO notifications.notifications
                    ("Id", "TenantId", "Kind", "Severity", "TitleKey", "BodyKey", "ParamsJson",
                     "DedupeKey", "CreatedAt")
                VALUES (@id, @tenant, 'site.published', 1, 't', 'b', '{}'::jsonb, @dedupe, now())
                """;
            cmd.Parameters.AddWithValue("id", notificationId);
            cmd.Parameters.AddWithValue("tenant", tenantId);
            cmd.Parameters.AddWithValue("dedupe", Guid.NewGuid().ToString("N"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var userId in userIds)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO notifications.recipients
                    ("Id", "TenantId", "NotificationId", "UserId", "CreatedAt")
                VALUES (gen_random_uuid(), @tenant, @notification, @user, now())
                """;
            cmd.Parameters.AddWithValue("tenant", tenantId);
            cmd.Parameters.AddWithValue("notification", notificationId);
            cmd.Parameters.AddWithValue("user", userId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return notificationId;
    }

    private static async Task<(Guid Id, string Slug)> CreateTenantAsync(HttpClient client, CancellationToken ct)
    {
        var slug = "notif-ep-" + Guid.NewGuid().ToString("N")[..8];
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Headers = { { "X-Test-Sub", Guid.NewGuid().ToString() }, { "X-Test-Roles", "SuperAdmin" } },
            Content = JsonContent.Create(new
            {
                slug,
                name = slug,
                ownerUserId = Guid.NewGuid(),
                ownerEmail = $"{slug}@dcms.test",
            }),
        };
        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return (json.GetProperty("tenantId").GetGuid(), slug);
    }
}
