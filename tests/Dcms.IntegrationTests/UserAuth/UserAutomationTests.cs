using System.Net;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.IntegrationTests.DynamicApps;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using NATS.Client.Core;
using NATS.Client.JetStream;

namespace Dcms.IntegrationTests.UserAuth;

/// <summary>
/// The directory in Dynamic Apps flows (ADR 0022, UA7): flows invite people and change their
/// groups through <c>automation.actions@1</c>, and trigger on <c>user.invited</c> and on
/// <c>user.activated</c>, which identity announces and admin-api relays.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class UserAutomationTests(ContentFlowFixture fixture)
{
    private const string Model = """
        [
          { "op": "create", "type": "table", "value": { "apiName": "people", "displayName": "Person",
              "fields": [ { "apiName": "email", "displayName": "Email", "required": true }, { "apiName": "how", "displayName": "How" } ] } },
          { "op": "create", "type": "flow", "value": { "apiName": "onboard", "displayName": "Onboard", "trigger": { "event": "manual" },
              "steps": [
                { "id": "invite", "action": "user-auth.invite@1", "input": { "email": "{{ input.email }}", "groups": [ "Staff" ] } },
                { "id": "again", "action": "user-auth.invite@1", "input": { "email": "{{ input.email }}" } } ] } },
          { "op": "create", "type": "flow", "value": { "apiName": "on_invited", "displayName": "On invited", "trigger": { "event": "user.invited" },
              "steps": [ { "id": "log", "action": "records.create@1", "input": { "table": "people", "values": { "email": "{{ event.payload.email }}", "how": "invited" } } } ] } },
          { "op": "create", "type": "flow", "value": { "apiName": "on_activated", "displayName": "On activated", "trigger": { "event": "user.activated" },
              "steps": [ { "id": "log", "action": "records.create@1", "input": { "table": "people", "values": { "email": "{{ event.payload.email }}", "how": "activated" } } } ] } }
        ]
        """;

    [DockerFact]
    public async Task A_flow_invites_into_a_group_and_the_invitation_starts_another_flow()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, tenantId) = await SetUpAsync(ct);
        var staff = (await JsonAsync(await AdminAsync(app, HttpMethod.Post, "/groups", ct, new { name = "Staff" }), HttpStatusCode.Created, ct))
            .GetProperty("id").GetGuid();
        await app.PublishAsync(Model, ct);

        await app.JsonAsync(HttpMethod.Post, "/_automation/flows/onboard/run", ct, new { input = new { email = "new@corp.test" } }, HttpStatusCode.Accepted);

        var people = await WaitForPeopleAsync(app, 1, ct);
        people.Single().GetProperty("email").GetString().Should().Be("new@corp.test", "the invitation the flow sent set off on_invited — once");
        var users = await JsonAsync(await AdminAsync(app, HttpMethod.Get, "/users", ct), HttpStatusCode.OK, ct);
        var invited = users.GetProperty("items").EnumerateArray().Single();
        invited.GetProperty("email").GetString().Should().Be("new@corp.test", "inviting twice is inviting once");
        invited.GetProperty("groups")[0].GetGuid().Should().Be(staff, "groups are named by name in a flow");
        fixture.Realms.RealmOf(tenantId).Should().NotBeNull();
    }

    [DockerFact]
    public async Task An_activation_identity_announces_starts_the_tenants_flows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, tenantId) = await SetUpAsync(ct);
        await app.PublishAsync(Model, ct);

        await using var nats = new NatsConnection(NatsOpts.Default with { Url = fixture.NatsUrl, SerializerRegistry = NATS.Client.Serializers.Json.NatsJsonSerializerRegistry.Default });
        var js = new NatsJSContext(nats);
        await js.PublishAsync(Subjects.RealmUserActivated,
            new RealmUserActivated(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, Guid.NewGuid(), "joined@corp.test"), cancellationToken: ct);

        var people = await WaitForPeopleAsync(app, 1, ct);
        people.Single().GetProperty("how").GetString().Should().Be("activated");
        people.Single().GetProperty("email").GetString().Should().Be("joined@corp.test");
    }

    private async Task<(AppHarness App, Guid TenantId)> SetUpAsync(CancellationToken ct)
    {
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.InstallAsync("user-auth", "users", "{}", ct);
        await using var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "Id"::uuid FROM tenancy.tenants WHERE "Identifier" = @p""";
        cmd.Parameters.AddWithValue("p", app.Tenant);
        return (app, (Guid)(await cmd.ExecuteScalarAsync(ct))!);
    }

    private static async Task<List<JsonElement>> WaitForPeopleAsync(AppHarness app, int count, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        List<JsonElement> items = [];
        while (DateTime.UtcNow < deadline)
        {
            items = (await app.JsonAsync(HttpMethod.Get, "/_records/people", ct)).GetProperty("items").EnumerateArray().ToList();
            if (items.Count >= count)
            {
                // A little longer, so a second record that should not exist has its chance to.
                await Task.Delay(1500, ct);
                return (await app.JsonAsync(HttpMethod.Get, "/_records/people", ct)).GetProperty("items").EnumerateArray().ToList();
            }
            await Task.Delay(300, ct);
        }
        var runs = await app.JsonAsync(HttpMethod.Get, "/_automation/runs", ct);
        throw new Xunit.Sdk.XunitException($"Expected {count} people, found {items.Count}. Runs: {runs}");
    }

    private static Task<HttpResponseMessage> AdminAsync(AppHarness app, HttpMethod method, string path, CancellationToken ct, object? body = null) =>
        app.Admin.SendAsync(AppHarness.Req(method, "/api/admin/plugins/users" + path, app.Owner, app.Tenant, body: body), ct);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.Should().Be(expected, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
