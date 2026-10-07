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

    [DockerFact]
    public async Task A_form_submission_on_the_site_triggers_a_flow_with_what_was_sent()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.InstallAsync("forms", "contact", """{"forms":[{"name":"hello","fields":[{"name":"message","required":true}]}]}""", ct);
        await app.PublishAsync(Crm, ct);
        await app.PublishAsync("""
            [ { "op": "create", "type": "flow", "value": { "apiName": "file_it", "displayName": "File it",
                "trigger": { "event": "form.submitted" }, "condition": "event.payload.formName == 'hello'",
                "steps": [ { "id": "log", "action": "records.create@1", "input": { "table": "log", "values": { "message": "{{ event.payload.data.message }}" } } } ] } } ]
            """, ct);

        var sent = await app.SiteAsync(HttpMethod.Post, "/api/contact/forms/hello", ct, new { message = "call me back" });
        ((int)sent.StatusCode).Should().BeInRange(200, 299, await sent.Content.ReadAsStringAsync(ct));

        var run = (await WaitForRunsAsync(app, "file_it", 1, ct)).Single();
        run.GetProperty("status").GetString().Should().Be("succeeded", run.ToString());
        run.GetProperty("trigger").GetString().Should().Be("form.submitted");
        (await app.JsonAsync(HttpMethod.Get, "/_records/log", ct)).GetProperty("items")[0].GetProperty("message").GetString()
            .Should().Be("call me back");
    }

    [DockerFact]
    public async Task A_new_visitor_triggers_a_flow_that_acts_through_another_plugins_action()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.InstallAsync("visitor-auth", "members",
            """{"attributes":[{"key":"company","label":"Company","visibility":"plugins"},{"key":"secret","label":"Secret","visibility":"private"}]}""", ct);
        await app.PublishAsync(Crm, ct);

        var actions = await app.JsonAsync(HttpMethod.Get, "/_automation/actions", ct);
        actions.EnumerateArray().Select(a => a.GetProperty("key").GetString()).Should().Contain("visitor-auth.set-attributes@1");

        await app.PublishAsync("""
            [ { "op": "create", "type": "flow", "value": { "apiName": "welcome", "displayName": "Welcome",
                "trigger": { "event": "visitor.registered" },
                "steps": [
                  { "id": "tag", "action": "visitor-auth.set-attributes@1",
                    "input": { "visitorId": "{{ event.payload.visitorId }}", "attributes": { "company": "Acme" } } },
                  { "id": "log", "action": "records.create@1",
                    "input": { "table": "log", "values": { "message": "{{ steps.tag.email }} at {{ steps.tag.attributes.company }}" } } } ] } } ]
            """, ct);

        var email = $"{Guid.NewGuid():N}@visitor.test";
        await app.VisitorAsync(email, ct);

        var run = (await WaitForRunsAsync(app, "welcome", 1, ct)).Single();
        run.GetProperty("status").GetString().Should().Be("succeeded", run.ToString());
        (await app.JsonAsync(HttpMethod.Get, "/_records/log", ct)).GetProperty("items")[0].GetProperty("message").GetString()
            .Should().Be($"{email} at Acme");

        // The provider's rules hold for a flow too: a private attribute is refused, and the step
        // fails without retrying.
        await app.PublishAsync("""
            [ { "op": "create", "type": "flow", "value": { "apiName": "pry", "displayName": "Pry",
                "steps": [ { "id": "tag", "action": "visitor-auth.set-attributes@1",
                  "input": { "visitorId": "{{ input.visitorId }}", "attributes": { "secret": "x" } } } ] } } ]
            """, ct);
        var detail = await app.JsonAsync(HttpMethod.Get, $"/_automation/runs/{run.GetProperty("id").GetString()}", ct);
        var registered = detail.GetProperty("steps")[0].GetProperty("input").GetProperty("visitorId").GetString();
        await app.JsonAsync(HttpMethod.Post, "/_automation/flows/pry/run", ct, new { input = new { visitorId = registered } }, HttpStatusCode.Accepted);
        var pried = (await WaitForRunsAsync(app, "pry", 1, ct)).Single();
        pried.GetProperty("status").GetString().Should().Be("failed");
        pried.GetProperty("attempts").GetInt32().Should().Be(1);

        // Publishing an app is not managing visitors: a member who may do the one but not the
        // other cannot write a flow that does it for them — yet may edit the rest of the app.
        var builder = await app.AddMemberAsync(
            ["plugin:dynamic-apps:model-read", "plugin:dynamic-apps:model-write", "plugin:dynamic-apps:publish"], ct);
        var hash = (await app.JsonAsync(HttpMethod.Get, "/_model", ct, @as: builder)).GetProperty("hash").GetString();
        var applied = await app.JsonAsync(HttpMethod.Post, "/_model/draft/changes", ct, new
        {
            expectedHash = hash,
            operations = AppHarness.Operations("""
                [ { "op": "update", "type": "flow", "target": "welcome", "value": { "displayName": "Welcome, again" } } ]
                """),
        }, @as: builder);
        Errors(applied).Should().Equal("action-not-permitted");
        (await app.SendAsync(HttpMethod.Post, "/_model/draft/publish",
                new { expectedHash = applied.GetProperty("draft").GetProperty("hash").GetString() }, ct, builder))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        hash = (await app.JsonAsync(HttpMethod.Get, "/_model", ct, @as: builder)).GetProperty("hash").GetString();
        await app.JsonAsync(HttpMethod.Post, "/_model/draft/discard", ct, new { expectedHash = hash }, HttpStatusCode.NoContent, builder);
        hash = (await app.JsonAsync(HttpMethod.Get, "/_model", ct, @as: builder)).GetProperty("hash").GetString();
        var unrelated = await app.JsonAsync(HttpMethod.Post, "/_model/draft/changes", ct, new
        {
            expectedHash = hash,
            operations = AppHarness.Operations("""[ { "op": "update", "type": "table", "target": "log", "value": { "displayName": "Journal" } } ]"""),
        }, @as: builder);
        Errors(unrelated).Should().BeEmpty("the visitor flow is unchanged from the live one");
    }

    [DockerFact]
    public async Task A_flow_naming_an_action_no_installed_plugin_offers_does_not_publish()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Crm, ct);
        var applied = await app.JsonAsync(HttpMethod.Post, "/_model/draft/changes", ct, new
        {
            expectedHash = (await app.JsonAsync(HttpMethod.Get, "/_model", ct)).GetProperty("hash").GetString(),
            operations = AppHarness.Operations("""
                [ { "op": "create", "type": "flow", "value": { "apiName": "tag", "displayName": "Tag",
                    "steps": [ { "id": "tag", "action": "visitor-auth.set-attributes@1", "input": {} } ] } } ]
                """),
        });
        Errors(applied).Should().Contain("unknown-action");
    }

    // ---- helpers ----

    private static IEnumerable<string?> Errors(JsonElement applied) => applied.GetProperty("issues").EnumerateArray()
        .Where(i => i.GetProperty("severity").GetString() == "error").Select(i => i.GetProperty("code").GetString());

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
