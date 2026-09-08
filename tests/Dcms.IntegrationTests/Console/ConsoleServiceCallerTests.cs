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

namespace Dcms.IntegrationTests.Console;

/// <summary>
/// admin-api answering the platform console's API instead of the console's browser.
///
/// <para>The console is moving off admin-api as a browser-facing origin: its SPA calls
/// platform-api, which calls here with a client-credentials token. That token carries a client
/// id rather than a user id, which breaks two things at once — the <c>IsSuperAdmin</c> check
/// every one of these endpoints is gated by, and the audit record, which would otherwise name
/// the last hop and say a service suspended a tenant.</para>
///
/// <para>Both halves are tested here because both are silent when wrong. A refused call is a
/// console page that 500s in a way that looks like a network fault; a misattributed record is
/// an audit log that reads perfectly and names the wrong actor.</para>
/// </summary>
[Collection(AdminApiCollection.Name)]
public class ConsoleServiceCallerTests(AdminApiFixture fixture)
{
    private const string ConsoleClient = "dcms-platform-api-service";
    private const string ConsoleScope = "dcms.console";

    private static readonly Guid SuperAdmin = Guid.NewGuid();

    [DockerFact]
    public async Task The_console_service_reaches_an_endpoint_gated_on_superadmin()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var res = await client.SendAsync(
            AsConsole(HttpMethod.Get, "/api/admin/platform/certificates", Guid.NewGuid()), ct);

        res.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the operator's permission was decided by platform-api; this service holds dcms.console "
            + "and the endpoint invited it");
    }

    [DockerFact]
    public async Task A_service_token_with_another_scope_is_still_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/platform/certificates");
        req.Headers.Add("X-Test-Sub", "dcms-admin-api");
        // The scope content-api holds. Its client secret is shared, so if this reached the
        // certificate list the public delivery plane could read the platform's TLS inventory.
        req.Headers.Add("X-Test-Scope", "dcms.social");

        var res = await client.SendAsync(req, ct);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [DockerFact]
    public async Task A_signed_in_non_superadmin_is_still_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/platform/certificates");
        req.Headers.Add("X-Test-Sub", Guid.NewGuid().ToString());
        req.Headers.Add("X-Test-Roles", "Support");

        var res = await client.SendAsync(req, ct);

        res.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "opening a door for the console API must not open one for a signed-in user");
    }

    /// <summary>
    /// The bell is keyed on one operator's id, which a client-credentials token does not carry.
    /// Without the propagated actor there is no id to key on, and the endpoint would either
    /// throw or — worse — silently show one shared bell to every operator.
    /// </summary>
    [DockerFact]
    public async Task The_bell_the_console_service_reads_belongs_to_the_operator_it_names()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        await RaiseAsync(ct);

        (await UnreadAsync(client, mine, ct)).Should().BeGreaterThan(0);

        var readAll = await client.SendAsync(
            AsConsole(HttpMethod.Post, "/api/admin/platform/notifications/read-all", mine), ct);
        readAll.EnsureSuccessStatusCode();

        (await UnreadAsync(client, mine, ct)).Should().Be(0);
        (await UnreadAsync(client, theirs, ct)).Should().BeGreaterThan(
            0, "the console service acted for one operator, not for every operator");
    }

    /// <summary>
    /// The record has to name the person, and has to say it heard so from a peer. Both halves
    /// matter: without the first the console's own audit page shows a service suspending
    /// tenants and no operator anywhere, and without the second a propagated identity would be
    /// indistinguishable from one this service authenticated itself.
    /// </summary>
    [DockerFact]
    public async Task A_suspension_through_the_console_is_recorded_against_the_operator()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var operatorId = Guid.NewGuid();
        var tenantId = await NewTenantAsync(client, ct);

        var res = await client.SendAsync(
            AsConsole(HttpMethod.Post, $"/api/admin/tenants/{tenantId}/suspend", operatorId,
                display: "ops@example.com"),
            ct);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var record = await ActorOfAsync("platform.tenant.suspended", tenantId.ToString(), ct);

        record.Should().NotBeNull("the suspension is audited, and this is the record of it");
        record!.Kind.Should().Be("user", "a person suspended this tenant, not a service");
        record.Id.Should().Be(operatorId);
        record.Display.Should().Be("ops@example.com");
        record.Attribution.Should().Be(
            "propagated",
            "this identity crossed a process boundary on a peer's word and the record says so");
    }

    /// <summary>
    /// The counter-test to the one above, and the reason the middleware checks for a service
    /// caller rather than trusting the headers outright. If a signed-in user could set these,
    /// anyone could write somebody else's name into the audit log.
    /// </summary>
    [DockerFact]
    public async Task A_signed_in_user_cannot_rewrite_their_own_attribution()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var tenantId = await NewTenantAsync(client, ct);
        var somebodyElse = Guid.NewGuid();

        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/tenants/{tenantId}/suspend");
        req.Headers.Add("X-Test-Sub", SuperAdmin.ToString());
        req.Headers.Add("X-Test-Roles", "SuperAdmin");
        req.Headers.Add("Dcms-Actor-Kind", "User");
        req.Headers.Add("Dcms-Actor-Id", somebodyElse.ToString());
        req.Headers.Add("Dcms-Actor-Display", "not-me@example.com");

        var res = await client.SendAsync(req, ct);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var record = await ActorOfAsync("platform.tenant.suspended", tenantId.ToString(), ct);

        record.Should().NotBeNull();
        record!.Id.Should().Be(SuperAdmin, "the actor is the one this service authenticated");
        record.Attribution.Should().Be("direct");
    }

    // ---------- helpers ----------

    /// <summary>Raises one platform notification, so the bell has something in it to clear.</summary>
    private async Task RaiseAsync(CancellationToken ct)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IPlatformNotificationPublisher>();

        var raised = await publisher.RaiseAsync(
            new PlatformNotificationRequest(
                "certificate.failed", NotificationSeverity.Error,
                $"console-test:{Guid.NewGuid():N}",
                new { name = "Platform wildcard" }, "/certificates"),
            ct);

        raised.Should().BeTrue();
    }

    /// <summary>
    /// A request as the console's API makes it: a client-credentials principal carrying
    /// <c>dcms.console</c>, plus the propagation headers naming the operator it acts for.
    /// </summary>
    private static HttpRequestMessage AsConsole(
        HttpMethod method, string url, Guid operatorId, string? display = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", ConsoleClient);
        req.Headers.Add("X-Test-Scope", ConsoleScope);
        req.Headers.Add("Dcms-Actor-Kind", "User");
        req.Headers.Add("Dcms-Actor-Id", operatorId.ToString());
        req.Headers.Add("Dcms-Actor-Display", display ?? $"{operatorId:N}@dcms.test");
        return req;
    }

    private async Task<Guid> NewTenantAsync(HttpClient client, CancellationToken ct)
    {
        var slug = "console-" + Guid.NewGuid().ToString("N")[..8];
        var owner = Guid.NewGuid();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Content = JsonContent.Create(new
            {
                slug,
                name = slug,
                ownerUserId = owner,
                ownerEmail = $"{owner:N}@dcms.test",
            }),
        };
        req.Headers.Add("X-Test-Sub", SuperAdmin.ToString());
        req.Headers.Add("X-Test-Roles", "SuperAdmin");

        var res = await client.SendAsync(req, ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return body.GetProperty("tenantId").GetGuid();
    }

    private async Task<int> UnreadAsync(HttpClient client, Guid operatorId, CancellationToken ct)
    {
        var res = await client.SendAsync(
            AsConsole(HttpMethod.Get, "/api/admin/platform/notifications/unread-count", operatorId), ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.GetProperty("unread").GetInt32();
    }

    private sealed record Actor(string Kind, Guid? Id, string? Display, string Attribution);

    /// <summary>
    /// The actor on the audit record for one action against one resource.
    ///
    /// <para>Polled, for the reason <c>AuditReadPlaneTests</c> spells out: the record commits to
    /// <c>audit.audit_outbox</c> inside the caller's own transaction and a dispatcher promotes it
    /// to <c>audit.audit_events</c> afterwards, so reading the instant the response arrives races
    /// that dispatcher.</para>
    /// </summary>
    private async Task<Actor?> ActorOfAsync(string action, string resourceId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT "ActorKind", "ActorId", "ActorDisplay", "ActorAttribution"
                FROM audit.audit_events
                WHERE "Action" = @a AND "ResourceId" = @r
                ORDER BY "Seq" DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("a", action);
            cmd.Parameters.AddWithValue("r", resourceId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return new Actor(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3));
            }

            await Task.Delay(250, ct);
        }

        return null;
    }
}
