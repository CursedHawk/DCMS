extern alias AdminApiApp;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Data.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

using IPlatformNotificationPublisher =
    AdminApiApp::Dcms.AdminApi.Notifications.IPlatformNotificationPublisher;
using PlatformNotificationRequest =
    AdminApiApp::Dcms.AdminApi.Notifications.PlatformNotificationRequest;

namespace Dcms.IntegrationTests.Notifications;

/// <summary>
/// The platform bell, whose access control and read model both differ from the tenant one.
///
/// <para>There are no recipient rows here: the audience is "every SuperAdmin", a global role
/// admin-api cannot enumerate, so the role check on the endpoint <i>is</i> the audience and
/// unread means "this operator has no read-state row". Those two facts are what these tests
/// pin, because both are places where a plausible-looking query silently shows an operator
/// somebody else's read state or nothing at all.</para>
/// </summary>
[Collection(AdminApiCollection.Name)]
public class PlatformNotificationTests(AdminApiFixture fixture)
{
    [DockerFact]
    public async Task Every_superadmin_sees_the_same_notification_and_their_own_read_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var id = await RaiseAsync("certificate.failed", NotificationSeverity.Error, ct);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        // No fan-out happened, so this is the whole audience question: both see it.
        (await ItemsAsync(client, first, ct)).Should().Contain(id);
        (await ItemsAsync(client, second, ct)).Should().Contain(id);

        // Measured as a delta, not an absolute. These notifications have no tenant and no
        // recipient list, so every row any other test raised is legitimately in both operators'
        // bells -- which is the property under test, and would make an absolute count a
        // statement about test ordering rather than about read state.
        var firstBefore = await UnreadAsync(client, first, ct);
        var secondBefore = await UnreadAsync(client, second, ct);

        var read = await client.SendAsync(
            Request(HttpMethod.Post, $"/api/admin/platform/notifications/{id}/read", first), ct);
        read.EnsureSuccessStatusCode();

        (await UnreadAsync(client, first, ct)).Should().Be(firstBefore - 1);
        (await UnreadAsync(client, second, ct)).Should().Be(secondBefore,
            "one operator reading a platform notification must not clear it for the others");
    }

    /// <summary>
    /// The badge has to be right on the very first visit, when the operator has no read-state
    /// rows at all. An unread count written as "rows where ReadAt is null" reads zero here — the
    /// bell would be silent precisely when everything is unread.
    /// </summary>
    [DockerFact]
    public async Task Unread_counts_notifications_an_operator_has_never_touched()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var operatorId = Guid.NewGuid();

        var before = await UnreadAsync(client, operatorId, ct);
        await RaiseAsync("certificate.blocked", NotificationSeverity.Warning, ct);

        (await UnreadAsync(client, operatorId, ct)).Should().Be(before + 1);
    }

    /// <summary>
    /// "Mark all read" has to clear rows this operator has never touched, which an UPDATE alone
    /// cannot do — there is nothing to update. The badge not clearing is the one failure this
    /// button has.
    /// </summary>
    [DockerFact]
    public async Task Mark_all_read_clears_notifications_with_no_read_state_yet()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var operatorId = Guid.NewGuid();

        await RaiseAsync("certificate.failed", NotificationSeverity.Error, ct);
        await RaiseAsync("certificate.expiring", NotificationSeverity.Warning, ct);
        (await UnreadAsync(client, operatorId, ct)).Should().BeGreaterThan(0);

        var res = await client.SendAsync(
            Request(HttpMethod.Post, "/api/admin/platform/notifications/read-all", operatorId), ct);
        res.EnsureSuccessStatusCode();

        (await UnreadAsync(client, operatorId, ct)).Should().Be(0);
    }

    [DockerFact]
    public async Task Dismissing_hides_it_from_that_operator_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var id = await RaiseAsync("certificate.issued", NotificationSeverity.Success, ct);
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        var res = await client.SendAsync(
            Request(HttpMethod.Post, $"/api/admin/platform/notifications/{id}/dismiss", mine), ct);
        res.EnsureSuccessStatusCode();

        (await ItemsAsync(client, mine, ct)).Should().NotContain(id);
        (await ItemsAsync(client, theirs, ct)).Should().Contain(id,
            "dismissing is hiding your own copy, not deleting the notification");
    }

    [DockerFact]
    public async Task A_non_superadmin_cannot_read_the_platform_bell()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/platform/notifications");
        req.Headers.Add("X-Test-Sub", Guid.NewGuid().ToString());
        req.Headers.Add("X-Test-Roles", "Support");

        var res = await client.SendAsync(req, ct);

        // With no recipient rows to filter on, the role check is the entire access control.
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The certificate worker re-reads a window of history on every pass, so raising the same
    /// fact twice is the normal case rather than the exceptional one.
    /// </summary>
    [DockerFact]
    public async Task Raising_the_same_fact_twice_records_it_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = $"certificate.attempt:{Guid.NewGuid():N}";

        var request = new PlatformNotificationRequest(
            "certificate.failed", NotificationSeverity.Error, key,
            new { name = "Platform wildcard" }, "/certificates");

        using var scope = fixture.Factory.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IPlatformNotificationPublisher>();

        (await publisher.RaiseAsync(request, ct)).Should().BeTrue();
        (await publisher.RaiseAsync(request, ct)).Should().BeFalse();

        (await CountAsync(key, ct)).Should().Be(1);
    }

    /// <summary>Raises one notification and returns its id, found by the key it was raised under.</summary>
    private async Task<Guid> RaiseAsync(string kind, NotificationSeverity severity, CancellationToken ct)
    {
        var key = $"{kind}:{Guid.NewGuid():N}";
        using var scope = fixture.Factory.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IPlatformNotificationPublisher>();

        var raised = await publisher.RaiseAsync(
            new PlatformNotificationRequest(
                kind, severity, key,
                new { name = "Platform wildcard", identifiers = new[] { "highgeek.eu" } },
                "/certificates"),
            ct);

        raised.Should().BeTrue();

        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "Id" FROM notifications.platform_notifications WHERE "DedupeKey" = @k""";
        cmd.Parameters.AddWithValue("k", key);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<int> CountAsync(string dedupeKey, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT count(*) FROM notifications.platform_notifications WHERE "DedupeKey" = @k
            """;
        cmd.Parameters.AddWithValue("k", dedupeKey);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, Guid userId)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", userId.ToString());
        req.Headers.Add("X-Test-Roles", "SuperAdmin");
        return req;
    }

    private async Task<List<Guid>> ItemsAsync(HttpClient client, Guid userId, CancellationToken ct)
    {
        var res = await client.SendAsync(
            Request(HttpMethod.Get, "/api/admin/platform/notifications?limit=100", userId), ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return [.. json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];
    }

    private async Task<int> UnreadAsync(HttpClient client, Guid userId, CancellationToken ct)
    {
        var res = await client.SendAsync(
            Request(HttpMethod.Get, "/api/admin/platform/notifications/unread-count", userId), ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.GetProperty("unread").GetInt32();
    }
}
