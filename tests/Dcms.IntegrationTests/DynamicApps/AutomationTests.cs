using System.Net;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;

namespace Dcms.IntegrationTests.DynamicApps;

/// <summary>
/// Flows end to end against the real worker (ADR 0021): a record change routes through the
/// outbox to a run, the run's condition and steps execute through the action catalog — records,
/// platform contracts, other flows, events — and its history is inspectable. Plus the limits
/// that stop runaway cascades, retries, and schedules.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class AutomationTests(ContentFlowFixture fixture)
{
    private const string Crm = """
        [
          { "op": "create", "type": "table", "value": { "apiName": "companies", "displayName": "Company",
              "fields": [ { "apiName": "name", "displayName": "Name", "required": true }, { "apiName": "owner_email", "displayName": "Owner", "type": "email" } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "deals", "displayName": "Deal",
              "fields": [ { "apiName": "title", "displayName": "Title", "required": true },
                          { "apiName": "amount", "displayName": "Amount", "type": "decimal" },
                          { "apiName": "flag", "displayName": "Flag", "readOnly": true },
                          { "apiName": "bumps", "displayName": "Bumps", "type": "integer", "default": 0 } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "log", "displayName": "Log",
              "fields": [ { "apiName": "message", "displayName": "Message", "required": true } ] } },
          { "op": "create", "type": "relationship", "value": { "apiName": "company", "sourceTableId": "deals", "targetTableId": "companies" } }
        ]
        """;

    [DockerFact]
    public async Task A_new_record_runs_its_flow_through_records_and_platform_contracts()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Crm, ct);
        await app.PublishAsync("""
            [ { "op": "create", "type": "flow", "value": { "apiName": "big_deal", "displayName": "Big deal",
                "trigger": { "event": "row.created", "tableId": "deals" }, "condition": "row.amount >= 10000",
                "steps": [
                  { "id": "company", "action": "records.lookup@1", "input": { "table": "companies", "id": "{{ row.company }}" } },
                  { "id": "flag", "action": "records.update@1", "input": { "table": "deals", "id": "{{ row.id }}", "values": { "flag": "big for {{ steps.company.name }}" } } },
                  { "id": "tell", "action": "dcms.notifications.raise@1", "input": { "title": "Big deal", "body": "{{ row.title }}" } },
                  { "id": "mail", "action": "dcms.email.send@1", "condition": "steps.company.owner_email != null",
                    "input": { "to": "{{ steps.company.owner_email }}", "subject": "New deal {{ row.title }}", "text": "Worth {{ row.amount }}" } } ] } } ]
            """, ct);

        var acme = await Create(app, "companies", new { name = "Acme", owner_email = "owner@acme.test" }, ct);
        var big = await Create(app, "deals", new { title = "Big one", amount = 25000, company = acme }, ct);
        await Create(app, "deals", new { title = "Small one", amount = 50, company = acme }, ct);

        var runs = await WaitForRunsAsync(app, "big_deal", 2, ct);
        runs.Select(r => r.GetProperty("status").GetString()).Should().BeEquivalentTo(["succeeded", "skipped"]);
        var succeeded = runs.Single(r => r.GetProperty("status").GetString() == "succeeded");

        (await app.JsonAsync(HttpMethod.Get, $"/_records/deals/{big}", ct)).GetProperty("flag").GetString()
            .Should().Be("big for Acme", "a flow may set a read-only field");

        var detail = await app.JsonAsync(HttpMethod.Get, $"/_automation/runs/{succeeded.GetProperty("id").GetString()}", ct);
        detail.GetProperty("steps").EnumerateArray().Select(s => (s.GetProperty("stepId").GetString(), s.GetProperty("status").GetString()))
            .Should().Equal(("company", "succeeded"), ("flag", "succeeded"), ("tell", "succeeded"), ("mail", "succeeded"));
        detail.GetProperty("steps")[3].GetProperty("input").GetProperty("to").GetString().Should().Be("owner@acme.test");
        detail.GetProperty("triggerEvent").GetProperty("eventName").GetString().Should().Be("row.created");

        // The flow's own write is one level deeper and in the same correlation; it starts nothing,
        // because no flow listens for updates.
        succeeded.GetProperty("depth").GetInt32().Should().Be(0);
        succeeded.GetProperty("writes").GetInt32().Should().Be(1);
    }

    [DockerFact]
    public async Task A_flow_that_retriggers_itself_is_stopped_by_the_cascade_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Crm, ct);
        await app.PublishAsync("""
            [ { "op": "create", "type": "flow", "value": { "apiName": "bump", "displayName": "Bump",
                "trigger": { "event": "row.updated", "tableId": "deals" },
                "steps": [ { "id": "again", "action": "records.update@1", "input": { "table": "deals", "id": "{{ row.id }}", "values": { "bumps": "{{ row.bumps + 1 }}" } } } ] } } ]
            """, ct);
        var deal = await Create(app, "deals", new { title = "Loop" }, ct);
        await app.JsonAsync(HttpMethod.Patch, $"/_records/deals/{deal}", ct, new { title = "Loop!" });

        var runs = await WaitForRunsAsync(app, "bump", 7, ct, done: r => r.Any(x => x.GetProperty("status").GetString() == "terminated"));
        var terminated = runs.Single(r => r.GetProperty("status").GetString() == "terminated");
        terminated.GetProperty("error").GetString().Should().Contain("cascaded");
        runs.Select(r => r.GetProperty("correlationId").GetGuid()).Distinct().Should().ContainSingle("one original change");
        runs.Max(r => r.GetProperty("depth").GetInt32()).Should().Be(6);

        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        (await RunsAsync(app, "bump", ct)).Should().HaveCount(runs.Count, "nothing runs after the limit");
        (await app.JsonAsync(HttpMethod.Get, $"/_records/deals/{deal}", ct)).GetProperty("bumps").GetInt32().Should().Be(6);
    }

    [DockerFact]
    public async Task Flows_chain_through_invoke_and_published_events_in_one_correlation()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Crm, ct);
        await app.PublishAsync("""
            [ { "op": "create", "type": "flow", "value": { "apiName": "write_log", "displayName": "Write log",
                "steps": [ { "id": "log", "action": "records.create@1", "input": { "table": "log", "values": { "message": "{{ input.message }}" } } },
                           { "id": "announce", "action": "event.publish@1", "input": { "name": "logged", "payload": { "id": "{{ steps.log.id }}" } } } ] } },
              { "op": "create", "type": "flow", "value": { "apiName": "on_logged", "displayName": "On logged",
                "trigger": { "event": "flow.event.logged" },
                "steps": [ { "id": "read", "action": "records.lookup@1", "input": { "table": "log", "id": "{{ event.payload.id }}" } } ] } },
              { "op": "create", "type": "flow", "value": { "apiName": "starter", "displayName": "Starter",
                "trigger": { "event": "row.created", "tableId": "companies" },
                "steps": [ { "id": "go", "action": "flow.invoke@1", "input": { "flow": "write_log", "input": { "message": "new company {{ row.name }}" } } } ] } } ]
            """, ct);

        await Create(app, "companies", new { name = "Initech" }, ct);

        var last = (await WaitForRunsAsync(app, "on_logged", 1, ct)).Single();
        last.GetProperty("status").GetString().Should().Be("succeeded");
        var chain = (await app.JsonAsync(HttpMethod.Get, $"/_automation/runs/{last.GetProperty("id").GetString()}", ct)).GetProperty("chain");
        chain.EnumerateArray().Select(r => (r.GetProperty("flow").GetString(), r.GetProperty("depth").GetInt32()))
            .Should().Equal(("starter", 0), ("write_log", 1), ("on_logged", 2));
        (await app.JsonAsync(HttpMethod.Get, "/_records/log", ct)).GetProperty("items")[0].GetProperty("message").GetString()
            .Should().Be("new company Initech");
    }

    [DockerFact]
    public async Task A_failed_run_is_kept_inspectable_and_can_be_retried_by_hand()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Crm, ct);
        await app.PublishAsync("""
            [ { "op": "create", "type": "flow", "value": { "apiName": "fix", "displayName": "Fix",
                "steps": [ { "id": "update", "action": "records.update@1", "input": { "table": "deals", "id": "{{ input.id }}", "values": { "title": "fixed" } } } ] } } ]
            """, ct);

        var started = await app.JsonAsync(HttpMethod.Post, "/_automation/flows/fix/run", ct, new { input = new { id = Guid.NewGuid() } }, HttpStatusCode.Accepted);
        var runId = started.GetProperty("runId").GetString();
        var failed = (await WaitForRunsAsync(app, "fix", 1, ct, done: r => r.All(x => x.GetProperty("status").GetString() == "failed"))).Single();
        failed.GetProperty("error").GetString().Should().Contain("There is no deals record");
        failed.GetProperty("attempts").GetInt32().Should().Be(1, "a missing record is not retried automatically");

        await app.JsonAsync(HttpMethod.Post, $"/_automation/runs/{runId}/retry", ct, expect: HttpStatusCode.Accepted);
        await WaitAsync(async () => (await app.JsonAsync(HttpMethod.Get, $"/_automation/runs/{runId}", ct))
            .GetProperty("steps").GetArrayLength() == 2, ct);
        (await app.SendAsync(HttpMethod.Post, $"/_automation/runs/{Guid.NewGuid()}/retry", null, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await app.SendAsync(HttpMethod.Post, "/_automation/flows/nope/run", new { }, ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task A_due_schedule_starts_its_flow_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Crm, ct);
        await app.PublishAsync("""
            [ { "op": "create", "type": "flow", "value": { "apiName": "tick", "displayName": "Tick",
                "trigger": { "event": "schedule", "everyMinutes": 60 },
                "steps": [ { "id": "log", "action": "records.create@1", "input": { "table": "log", "values": { "message": "tick" } } } ] } } ]
            """, ct);

        // Due now: as if an hour had passed.
        await using (var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE apps.flow_schedules SET "NextRunAt" = now() - interval '1 minute'
                WHERE "TenantId" = (SELECT "Id"::uuid FROM tenancy.tenants WHERE "Identifier" = @slug)
                """;
            cmd.Parameters.AddWithValue("slug", app.Tenant);
            (await cmd.ExecuteNonQueryAsync(ct)).Should().Be(1, "publishing created the schedule");
        }

        var run = (await WaitForRunsAsync(app, "tick", 1, ct, done: r => r.All(x => x.GetProperty("status").GetString() == "succeeded"))).Single();
        run.GetProperty("trigger").GetString().Should().Be("schedule");
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        (await RunsAsync(app, "tick", ct)).Should().ContainSingle("the schedule moved on to its next due time");
    }

    // ---- helpers ----

    private static async Task<string> Create(AppHarness app, string table, object values, CancellationToken ct) =>
        (await app.JsonAsync(HttpMethod.Post, $"/_records/{table}", ct, values, HttpStatusCode.Created)).GetProperty("id").GetString()!;

    private static async Task<List<JsonElement>> RunsAsync(AppHarness app, string flow, CancellationToken ct) =>
        (await app.JsonAsync(HttpMethod.Get, $"/_automation/runs?flow={flow}&pageSize=100", ct)).GetProperty("items").EnumerateArray().ToList();

    /// <summary>Waits until the flow has at least <paramref name="count"/> runs, all finished (or <paramref name="done"/> holds).</summary>
    private static async Task<List<JsonElement>> WaitForRunsAsync(AppHarness app, string flow, int count, CancellationToken ct,
        Func<List<JsonElement>, bool>? done = null)
    {
        string[] finished = ["succeeded", "skipped", "failed", "terminated"];
        List<JsonElement> runs = [];
        try
        {
            await WaitAsync(async () =>
            {
                runs = await RunsAsync(app, flow, ct);
                return done is not null
                    ? runs.Count > 0 && done(runs)
                    : runs.Count >= count && runs.All(r => finished.Contains(r.GetProperty("status").GetString()));
            }, ct);
        }
        catch (Xunit.Sdk.XunitException)
        {
            throw new Xunit.Sdk.XunitException($"Runs of {flow} never settled: {string.Join("; ", runs.Select(r => r.ToString()))}");
        }
        return runs;
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        for (var i = 0; i < 120; i++)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(250, ct);
        }
        throw new Xunit.Sdk.XunitException("Timed out waiting for the automation worker.");
    }
}
