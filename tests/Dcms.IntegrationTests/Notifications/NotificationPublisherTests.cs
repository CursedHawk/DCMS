extern alias AdminApiApp;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Data.Notifications;
using Dcms.Shared.Security;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

using NotificationRequest = AdminApiApp::Dcms.AdminApi.Notifications.NotificationRequest;
using INotificationPublisher = AdminApiApp::Dcms.AdminApi.Notifications.INotificationPublisher;

namespace Dcms.IntegrationTests.Notifications;

/// <summary>
/// The two properties the notification system rests on: the audience is decided by permission
/// at raise time, and an at-least-once redelivery cannot notify twice.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class NotificationPublisherTests(AdminApiFixture fixture)
{
    [DockerFact]
    public async Task Only_members_holding_the_gating_permission_receive_a_notification()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        // The owner gets every platform permission; the second member gets the Member role,
        // which holds media:read and analytics:read but not site:publish.
        var owner = Guid.NewGuid();
        var (tenantId, slug) = await CreateTenantAsync(client, owner, ct);
        var outsider = await SeedMemberWithRoleAsync(tenantId, "Member", ct);

        await RaiseAsync(new NotificationRequest(
            TenantId: tenantId,
            Kind: "site.published",
            Severity: NotificationSeverity.Success,
            RequiredPermission: PlatformPermissions.SitePublish,
            TitleKey: "t",
            BodyKey: "b",
            DedupeKey: Guid.NewGuid().ToString("N")), ct);

        var recipients = await RecipientsAsync(tenantId, ct);

        recipients.Should().Contain(owner,
            "the Owner role carries every platform permission, including site:publish");
        recipients.Should().NotContain(outsider,
            "a member without site:publish must not learn that a site was deployed");
    }

    [DockerFact]
    public async Task A_redelivered_event_notifies_only_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var owner = Guid.NewGuid();
        var (tenantId, _) = await CreateTenantAsync(client, owner, ct);

        // The same DedupeKey twice is exactly what JetStream at-least-once delivery produces.
        var dedupeKey = Guid.NewGuid().ToString("N");
        var request = new NotificationRequest(
            TenantId: tenantId,
            Kind: "site.published",
            Severity: NotificationSeverity.Success,
            RequiredPermission: PlatformPermissions.SitePublish,
            TitleKey: "t",
            BodyKey: "b",
            DedupeKey: dedupeKey);

        var first = await RaiseAsync(request, ct);
        var second = await RaiseAsync(request, ct);

        first.Should().BeGreaterThan(0);
        second.Should().Be(0, "the second delivery is a duplicate and must insert nothing");

        (await NotificationCountAsync(tenantId, dedupeKey, ct)).Should().Be(1);
    }

    [DockerFact]
    public async Task A_tenant_with_nobody_holding_the_permission_raises_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var (tenantId, _) = await CreateTenantAsync(client, Guid.NewGuid(), ct);

        // No role in a freshly provisioned tenant carries this plugin permission.
        var raised = await RaiseAsync(new NotificationRequest(
            TenantId: tenantId,
            Kind: "plugin.instance.changed",
            Severity: NotificationSeverity.Info,
            RequiredPermission: PlatformPermissions.ForPlugin("nobody-has-this", "read"),
            TitleKey: "t",
            BodyKey: "b",
            DedupeKey: Guid.NewGuid().ToString("N")), ct);

        raised.Should().Be(0);
    }

    private async Task<int> RaiseAsync(NotificationRequest request, CancellationToken ct)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<INotificationPublisher>();
        return await publisher.RaiseAsync(request, ct);
    }

    private async Task<List<Guid>> RecipientsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "UserId" FROM notifications.recipients WHERE "TenantId" = @t""";
        cmd.Parameters.AddWithValue("t", tenantId);

        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetGuid(0));
        }
        return ids;
    }

    private async Task<int> NotificationCountAsync(Guid tenantId, string dedupeKey, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT count(*) FROM notifications.notifications
            WHERE "TenantId" = @t AND "DedupeKey" = @k
            """;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("k", dedupeKey);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<(Guid Id, string Slug)> CreateTenantAsync(
        HttpClient client, Guid ownerUserId, CancellationToken ct)
    {
        var slug = "notif-" + Guid.NewGuid().ToString("N")[..8];
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Headers = { { "X-Test-Sub", Guid.NewGuid().ToString() }, { "X-Test-Roles", "SuperAdmin" } },
            Content = JsonContent.Create(new
            {
                slug,
                name = slug,
                ownerUserId,
                ownerEmail = $"{slug}@dcms.test",
            }),
        };
        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return (json.GetProperty("tenantId").GetGuid(), slug);
    }

    /// <summary>
    /// Seeds a member holding one named seeded role, in SQL. There is no "create member"
    /// endpoint — membership is only ever created by accepting an invitation — and driving the
    /// whole invitation flow would test the invitation code, not the notification audience.
    /// </summary>
    private async Task<Guid> SeedMemberWithRoleAsync(Guid tenantId, string roleName, CancellationToken ct)
    {
        var userId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);

        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO tenancy.tenant_memberships ("Id", "TenantId", "UserId", "Email", "CreatedAt")
                VALUES (@id, @tenant, @user, @email, now());

                INSERT INTO tenancy.member_roles ("Id", "TenantId", "MembershipId", "TenantRoleId")
                SELECT gen_random_uuid(), @tenant, @id, r."Id"
                FROM tenancy.tenant_roles r
                WHERE r."TenantId" = @tenant AND r."Name" = @role;
                """;
            insert.Parameters.AddWithValue("id", membershipId);
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("user", userId);
            insert.Parameters.AddWithValue("email", $"member-{userId:N}@dcms.test");
            insert.Parameters.AddWithValue("role", roleName);
            await insert.ExecuteNonQueryAsync(ct);
        }

        return userId;
    }
}
