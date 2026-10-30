extern alias AdminApiApp;
extern alias ScriptRunnerApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Automation;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Plugins.Functions.Build;
using Dcms.Plugins.Functions.Runtime;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dcms.IntegrationTests.Functions;

/// <summary>
/// Build and deploy (ADR 0029, F3), against a real Forgejo and the real script-runner: code
/// written through the IDE's routes and merged into release builds into a deployment, which
/// goes live only when someone holding what it uses deploys it; a broken build says where.
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class FunctionDeployTests(AdminApiFixture fixture, ScriptSandboxTests.Deno deno, FunctionRunnerTests.Esbuild esbuild)
    : IClassFixture<ScriptSandboxTests.Deno>, IClassFixture<FunctionRunnerTests.Esbuild>, IAsyncLifetime
{
    private TestForgejo? _forgejo;
    private WebApplicationFactory<ScriptRunnerApp::Program>? _runner;

    public async ValueTask InitializeAsync()
    {
        if (!DockerFactAttribute.Available)
        {
            return;
        }
        _forgejo = await TestForgejo.StartAsync();
        _runner = new WebApplicationFactory<ScriptRunnerApp::Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ScriptRunner:DenoPath", deno.Path);
            b.UseSetting("ScriptRunner:EsbuildPath", esbuild.Path);
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_runner is not null) await _runner.DisposeAsync();
        if (_forgejo is not null) await _forgejo.DisposeAsync();
    }

    [DockerFact]
    public async Task Code_merged_into_release_builds_and_goes_live_when_someone_holding_what_it_uses_deploys_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = Host();
        var client = host.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var instanceId = await InstallAsync(client, slug, ct);
        var owner = await SeedMemberAsync(tenantId, "Owner", ct);
        var member = await SeedMemberAsync(tenantId, "Member", ct);
        var api = "/api/admin/plugins/fn/_functions";

        // Opening the code creates the repository with the starter function.
        var repo = await Json(await client.SendAsync(Req(HttpMethod.Get, $"{api}/repo", slug, owner), ct), ct);
        var at = $"/api/admin/repos/{repo.GetProperty("id").GetGuid()}";
        var ide = await Json(await client.SendAsync(Req(HttpMethod.Get, $"{at}/ide", slug, owner), ct), ct);
        ide.GetProperty("files").TryGetProperty("functions/hello.ts", out _).Should().BeTrue();

        var orders = """
            import { defineFunction, on } from "@dcms/functions";
            import { total } from "../lib/money.ts";
            export default defineFunction({
              triggers: [on.row("shop.orders").updated({ fields: ["status"] }), on.every("1h")],
              access: { http: ["apiKey"] },
              uses: { tables: { "shop.orders": ["read", "update"] }, secrets: ["STRIPE_KEY"], egress: ["api.stripe.com"] },
              async handler(input: { lines: number[] }) { return total(input.lines); },
            });
            """;
        var sha = await ShipAsync(client, slug, owner, at, new()
        {
            ["functions/orders.ts"] = orders,
            ["lib/money.ts"] = "export const total = (xs: number[]): number => xs.reduce((a, b) => a + b, 0);\n",
        }, "Orders", ct);

        await BuildAsync(host, tenantId, instanceId, sha, ct);
        var first = (await Json(await client.SendAsync(Req(HttpMethod.Get, $"{api}/deployments", slug, owner), ct), ct))
            .EnumerateArray().Single();
        first.GetProperty("status").GetString().Should().Be("Ready", first.ToString());
        first.GetProperty("functions").EnumerateArray().Select(f => f.GetString()).Should().Equal("hello", "orders");
        var firstId = first.GetProperty("id").GetGuid();

        var detail = await Json(await client.SendAsync(Req(HttpMethod.Get, $"{api}/deployments/{firstId}", slug, owner), ct), ct);
        detail.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().BeEquivalentTo(
            "plugin:dynamic-apps:data-read", "plugin:dynamic-apps:data-write", "plugin:functions:secrets", "plugin:functions:deploy");
        var spec = detail.GetProperty("functions").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "orders");
        spec.GetProperty("uses").GetProperty("egress").EnumerateArray().Single().GetString().Should().Be("api.stripe.com");

        // A plain member holds no deploy permission; an owner holds everything it uses.
        (await client.SendAsync(Req(HttpMethod.Post, $"{api}/deployments/{firstId}/activate", slug, member), ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var live = await Json(await client.SendAsync(Req(HttpMethod.Post, $"{api}/deployments/{firstId}/activate", slug, owner), ct), ct);
        live.GetProperty("status").GetString().Should().Be("Active");
        (await VouchedAsync(firstId, ct)).Should().Contain("plugin:functions:secrets");

        // A type error fails the build, saying where; nothing changes for what is live.
        var broken = await ShipAsync(client, slug, owner, at, new()
        {
            ["functions/orders.ts"] = orders.Replace("return total(input.lines);", "const n: string = total(input.lines); return n;"),
        }, "Break it", ct);
        await BuildAsync(host, tenantId, instanceId, broken, ct);
        var failed = (await Json(await client.SendAsync(Req(HttpMethod.Get, $"{api}/deployments", slug, owner), ct), ct)).EnumerateArray().First();
        failed.GetProperty("status").GetString().Should().Be("Failed");
        var failure = await Json(await client.SendAsync(Req(HttpMethod.Get, $"{api}/deployments/{failed.GetProperty("id").GetGuid()}", slug, owner), ct), ct);
        var diagnostic = failure.GetProperty("diagnostics").EnumerateArray().Single();
        diagnostic.GetProperty("file").GetString().Should().Be("functions/orders.ts");
        diagnostic.GetProperty("line").GetInt32().Should().Be(7);
        (await client.SendAsync(Req(HttpMethod.Post, $"{api}/deployments/{failed.GetProperty("id").GetGuid()}/activate", slug, owner), ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Fixed and deployed, then rolled back to the first: the newer one is marked rolled back.
        var fixedSha = await ShipAsync(client, slug, owner, at, new() { ["functions/orders.ts"] = orders.Replace("total(input.lines)", "total(input.lines) * 2") }, "Fix", ct);
        await BuildAsync(host, tenantId, instanceId, fixedSha, ct);
        var second = (await Json(await client.SendAsync(Req(HttpMethod.Get, $"{api}/deployments", slug, owner), ct), ct)).EnumerateArray().First();
        second.GetProperty("status").GetString().Should().Be("Ready");
        await Json(await client.SendAsync(Req(HttpMethod.Post, $"{api}/deployments/{second.GetProperty("id").GetGuid()}/activate", slug, owner), ct), ct);
        await Json(await client.SendAsync(Req(HttpMethod.Post, $"{api}/deployments/{firstId}/activate", slug, owner), ct), ct);
        var statuses = (await Json(await client.SendAsync(Req(HttpMethod.Get, $"{api}/deployments", slug, owner), ct), ct))
            .EnumerateArray().Select(d => d.GetProperty("status").GetString()).ToList();
        statuses.Should().Equal("RolledBack", "Failed", "Active");
    }

    [DockerFact]
    public async Task A_push_to_release_queues_a_build_of_that_commit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = Host();
        var client = host.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var instanceId = await InstallAsync(client, slug, ct);

        var runner = host.Services.GetRequiredService<PluginHandlerRunner>();
        await runner.RunAsync(tenantId, "functions", null, (ctx, sp) =>
            ActivatorUtilities.CreateInstance<RepoPushedHandler>(sp).HandleAsync(new RepoPushed(instanceId, tenantId, "functions", "main", "aaa"), ctx, ct), ct);
        await runner.RunAsync(tenantId, "functions", null, (ctx, sp) =>
            ActivatorUtilities.CreateInstance<RepoPushedHandler>(sp).HandleAsync(new RepoPushed(instanceId, tenantId, "functions", "release", "bbb"), ctx, ct), ct);

        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "DedupeKey" FROM jobs.jobs WHERE "TenantId" = @tenant""";
        cmd.Parameters.AddWithValue("tenant", tenantId);
        var keys = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) keys.Add(reader.GetString(0));
        }
        keys.Should().ContainSingle().Which.Should().EndWith($"functions-build:{instanceId}:bbb:0", "only release builds");
    }

    [DockerFact]
    public async Task A_deployed_function_runs_with_only_what_it_declared_and_its_deployer_vouched_for()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = Host();
        var client = host.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var instanceId = await InstallAsync(client, slug, ct);
        var owner = await SeedMemberAsync(tenantId, "Owner", ct);
        var api = "/api/admin/plugins/fn/_functions";

        // A Dynamic Apps app with a table, and a secret for the functions.
        await PublishCrmAsync(client, slug, owner, ct);
        // Secrets: set through the app's screen, listed by name only, never read back.
        (await client.SendAsync(Req(HttpMethod.Put, $"{api}/secrets/STRIPE_KEY", slug, owner, new { value = "sk_test_1" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.SendAsync(Req(HttpMethod.Put, $"{api}/secrets/GONE", slug, owner, new { value = "x" }), ct)).EnsureSuccessStatusCode();
        (await client.SendAsync(Req(HttpMethod.Delete, $"{api}/secrets/GONE", slug, owner), ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var names = await (await client.SendAsync(Req(HttpMethod.Get, $"{api}/secrets", slug, owner), ct)).Content.ReadAsStringAsync(ct);
        names.Should().Be("""["STRIPE_KEY"]""");

        // The SDK the editor and the build check against: this environment's tables, typed.
        var sdk = await client.SendAsync(Req(HttpMethod.Get, $"{api}/sdk.d.ts", slug, owner), ct);
        var typings = await sdk.Content.ReadAsStringAsync(ct);
        typings.Should().Contain("export interface CrmCompaniesRow").And.Contain("companies: Table<CrmCompaniesRow>");
        var unchanged = Req(HttpMethod.Get, $"{api}/sdk.d.ts", slug, owner);
        unchanged.Headers.TryAddWithoutValidation("If-None-Match", sdk.Headers.ETag!.ToString());
        (await client.SendAsync(unchanged, ct)).StatusCode.Should().Be(HttpStatusCode.NotModified);
        // A reader of the functions who may not see the app's model does not see its tables.
        var reader = await SeedRoleAsync(tenantId, ["plugin:functions:read"], ct);
        var narrow = await (await client.SendAsync(Req(HttpMethod.Get, $"{api}/sdk.d.ts", slug, reader), ct)).Content.ReadAsStringAsync(ct);
        narrow.Should().Contain("export interface Database").And.NotContain("CrmCompaniesRow").And.NotContain("queryRecords");

        var repo = await Json(await client.SendAsync(Req(HttpMethod.Get, $"{api}/repo", slug, owner), ct), ct);
        var at = $"/api/admin/repos/{repo.GetProperty("id").GetGuid()}";
        var sha = await ShipAsync(client, slug, owner, at, new()
        {
            ["functions/onboard.ts"] = """
                import { defineFunction } from "@dcms/functions";
                export default defineFunction({
                  uses: { tables: { "crm.companies": ["read", "create"] }, secrets: ["STRIPE_KEY"], egress: ["api.example.com"],
                          contracts: ["dynamic-apps.records@1"] },
                  async handler(input: { name: string }, ctx) {
                    const db = ctx.db; // generated: crm.companies and crm.deals, typed
                    const created = await db.crm.companies.create({ name: input.name });
                    const typed: string | null = created.name;
                    const count = await db.crm.companies.count();
                    const key = await ctx.secrets.get("STRIPE_KEY");
                    const reply = await ctx.http.fetch("https://api.example.com/ping", { headers: { authorization: `Bearer ${key}` } });
                    const doubled = await ctx.functions.invoke<number>("double", { n: count });
                    const job = await ctx.jobs.enqueue("double", { n: 1 }, { dedupeKey: "once" });
                    const viaContract = await ctx.plugins.crm.dynamicAppsRecords.getRecord({ table: "companies", id: created.id });
                    const refusals: string[] = [];
                    for (const attempt of [() => db.crm.deals.find(), () => db.crm.companies.delete(created.id),
                                           () => ctx.http.fetch("https://evil.example.org/"), () => ctx.secrets.get("OTHER"),
                                           () => db.crm.companies.find({ expand: ["deal"] }),
                                           () => db.crm.companies.count({ field: "deal.title", op: "eq", value: "x" }),
                                           () => ctx.plugins.crm.dynamicAppsConfig.getSummary({})]) {
                      try { await attempt(); refusals.push("allowed"); } catch (e) { refusals.push((e as Error).message); }
                    }
                    ctx.log.info("created", created.name);
                    return { name: created.name, count, status: reply.status, pong: await reply.text(), doubled, job: job.id, viaContract: viaContract?.name ?? null, refusals };
                  },
                });
                """,
            ["functions/double.ts"] = """
                import { defineFunction } from "@dcms/functions";
                export default defineFunction({ handler: (input: { n: number }) => input.n * 2 });
                """,
        }, "Onboarding", ct);
        await BuildAsync(host, tenantId, instanceId, sha, ct);
        var built = (await Json(await client.SendAsync(Req(HttpMethod.Get, $"{api}/deployments", slug, owner), ct), ct)).EnumerateArray().First();
        built.GetProperty("status").GetString().Should().Be("Ready", built.ToString());
        var builtId = built.GetProperty("id").GetGuid();

        // Nothing is live yet; the build can be tried, under the caller's permissions.
        (await client.SendAsync(Req(HttpMethod.Post, $"{api}/run", slug, owner, new { function = "double", input = new { n = 2 } }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        var tried = await Json(await client.SendAsync(Req(HttpMethod.Post, $"{api}/run", slug, owner,
            new { function = "double", input = new { n = 21 }, deployment = builtId }), ct), ct);
        tried.GetProperty("result").GetInt32().Should().Be(42);

        await Json(await client.SendAsync(Req(HttpMethod.Post, $"{api}/deployments/{builtId}/activate", slug, owner), ct), ct);
        var run = await Json(await client.SendAsync(Req(HttpMethod.Post, $"{api}/run", slug, owner,
            new { function = "onboard", input = new { name = "Acme" } }), ct), ct);
        run.GetProperty("status").GetString().Should().Be("ok", run.ToString());
        var result = run.GetProperty("result");
        result.GetProperty("name").GetString().Should().Be("Acme");
        result.GetProperty("count").GetInt32().Should().Be(1);
        result.GetProperty("pong").GetString().Should().Be("pong for Bearer sk_test_1", "the secret reached only the declared host");
        result.GetProperty("doubled").GetInt32().Should().Be(2);
        var refusals = result.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()).ToList();
        refusals[0].Should().Contain("uses.tables[\"crm.deals\"]");
        refusals[1].Should().Contain("\"delete\"");
        refusals[2].Should().Contain("uses.egress");
        refusals[3].Should().Contain("uses.secrets");
        refusals[4].Should().Contain("Following companies.deal reads deals", "a lookup leads into another table");
        refusals[5].Should().Contain("Following companies.deal reads deals");
        refusals[6].Should().Contain("uses.contracts: [\"dynamic-apps.config@1\"]");
        result.GetProperty("viaContract").GetString().Should().Be("Acme", "a declared contract runs with the vouched permissions");
        run.GetProperty("logs").GetString().Should().Contain("[info] created Acme");
        _egress.Requests.Should().ContainSingle().Which.Should().Be("https://api.example.com/ping");

        // Running it acts with what it was vouched for, which the deploy permission alone is not.
        var deployer = await SeedRoleAsync(tenantId, ["plugin:functions:read", "plugin:functions:deploy"], ct);
        var refused = await client.SendAsync(Req(HttpMethod.Post, $"{api}/run", slug, deployer, new { function = "double", input = new { n = 1 } }), ct);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync(ct)).Should().Contain("plugin:dynamic-apps:data-read");

        // jobs.enqueue: a tracked run of the live deployment, once per dedupe key.
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "DedupeKey" FROM jobs.jobs WHERE "Id" = @id""";
        cmd.Parameters.AddWithValue("id", result.GetProperty("job").GetGuid());
        ((string)(await cmd.ExecuteScalarAsync(ct))!).Should().EndWith($"functions-run:{instanceId}:once");
    }

    private readonly StubEgress _egress = new();

    /// <summary>The internet, for http.fetch: one host that answers, and a record of what was asked.</summary>
    private sealed class StubEgress : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"pong for {request.Headers.Authorization}"),
            });
        }
    }

    [DockerFact]
    public async Task Row_events_messages_and_schedules_start_runs_and_a_cascade_stops_at_its_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = Host();
        var client = host.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var instanceId = await InstallAsync(client, slug, ct);
        var owner = await SeedMemberAsync(tenantId, "Owner", ct);
        var member = await SeedMemberAsync(tenantId, "Member", ct);
        var api = "/api/admin/plugins/fn/_functions";
        await PublishCrmAsync(client, slug, owner, ct);

        var repo = await Json(await client.SendAsync(Req(HttpMethod.Get, $"{api}/repo", slug, owner), ct), ct);
        var sha = await ShipAsync(client, slug, owner, $"/api/admin/repos/{repo.GetProperty("id").GetGuid()}", new()
        {
            ["functions/welcome.ts"] = """
                import { defineFunction, on } from "@dcms/functions";
                export default defineFunction({
                  triggers: [on.row("crm.companies").created()],
                  uses: { messages: { emit: ["companies.new"] } },
                  async handler(input: { record: { name: string } }, ctx) {
                    await ctx.messages.emit("companies.new", { name: input.record.name });
                    return input.record.name;
                  },
                });
                """,
            ["functions/greet.ts"] = """
                import { defineFunction, on } from "@dcms/functions";
                export default defineFunction({ triggers: [on.message("companies.new")], handler: (input: { name: string }) => `hello ${input.name}` });
                """,
            ["functions/shout.ts"] = """
                import { defineFunction, on } from "@dcms/functions";
                export default defineFunction({
                  triggers: [on.row("crm.deals").updated({ fields: ["title"] })],
                  uses: { tables: { "crm.deals": ["update"] } },
                  async handler(input: { id: string; record: { title: string } }, ctx) {
                    await ctx.db.crm.deals.update(input.id, { title: input.record.title + "!" });
                  },
                });
                """,
            ["functions/tick.ts"] = """
                import { defineFunction, on } from "@dcms/functions";
                export default defineFunction({ triggers: [on.every("5m")], handler: () => "tick" });
                """,
        }, "Triggers", ct);
        await BuildAsync(host, tenantId, instanceId, sha, ct);
        var built = (await Json(await client.SendAsync(Req(HttpMethod.Get, $"{api}/deployments", slug, owner), ct), ct)).EnumerateArray().First();
        built.GetProperty("status").GetString().Should().Be("Ready", built.ToString());
        // A row trigger hands its function the row: deploying it takes reading rows, though no function declares a read.
        var needs = await Json(await client.SendAsync(Req(HttpMethod.Get, $"{api}/deployments/{built.GetProperty("id").GetGuid()}", slug, owner), ct), ct);
        needs.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().Contain("plugin:dynamic-apps:data-read");
        await Json(await client.SendAsync(Req(HttpMethod.Post, $"{api}/deployments/{built.GetProperty("id").GetGuid()}/activate", slug, owner), ct), ct);

        // A row created: its function runs, and the message it emits starts another.
        await Json(await client.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/crm/_records/companies", slug, owner, new { name = "Acme" }), ct), ct);
        await DrainAsync(host, tenantId, ct);
        var runs = await RunsAsync(tenantId, ct);
        runs.Should().ContainSingle(r => r.Function == "welcome").Which.Should().Be(("welcome", "Succeeded", "\"Acme\"", 1));
        runs.Should().ContainSingle(r => r.Function == "greet").Which.Should().Be(("greet", "Succeeded", "\"hello Acme\"", 2));

        // A function that rewrites what triggers it: the cascade stops at the depth limit.
        var deal = await Json(await client.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/crm/_records/deals", slug, owner, new { title = "x" }), ct), ct);
        await Json(await client.SendAsync(Req(HttpMethod.Patch, $"/api/admin/plugins/crm/_records/deals/{deal.GetProperty("id").GetString()}", slug, owner,
            new { title = "deal" }), ct), ct);
        await DrainAsync(host, tenantId, ct);
        var shouts = (await RunsAsync(tenantId, ct)).Where(r => r.Function == "shout").ToList();
        shouts.Count(r => r.Status == "Succeeded").Should().Be(RunQueue.MaxDepth);
        shouts.Should().ContainSingle(r => r.Status == "Terminated").Which.Depth.Should().Be(RunQueue.MaxDepth + 1);

        // A schedule's slot starts once, however often the worker looks.
        var worker = ActivatorUtilities.CreateInstance<RunWorker>(host.Services);
        var now = DateTimeOffset.UtcNow;
        (await worker.ScheduleAsync(now, ct)).Should().BeGreaterThanOrEqualTo(1);
        (await RunsAsync(tenantId, ct)).Count(r => r.Function == "tick").Should().Be(1);
        await worker.ScheduleAsync(now, ct);
        (await RunsAsync(tenantId, ct)).Count(r => r.Function == "tick").Should().Be(1);

        // The history, for those who may read what runs carry.
        var page = await Json(await client.SendAsync(Req(HttpMethod.Get, $"{api}/runs?function=greet", slug, owner), ct), ct);
        var greetId = page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        var detail = await Json(await client.SendAsync(Req(HttpMethod.Get, $"{api}/runs/{greetId}", slug, owner), ct), ct);
        detail.GetProperty("input").GetProperty("name").GetString().Should().Be("Acme");
        (await client.SendAsync(Req(HttpMethod.Get, $"{api}/runs", slug, member), ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>Routes the apps' outbox and runs every queued run until nothing is left, as the workers would.</summary>
    private async Task DrainAsync(WebApplicationFactory<AdminApiApp::Program> host, Guid tenantId, CancellationToken ct)
    {
        var router = ActivatorUtilities.CreateInstance<Dcms.Plugins.DynamicApps.Automation.AutomationWorker>(host.Services);
        var worker = ActivatorUtilities.CreateInstance<RunWorker>(host.Services);
        for (var round = 0; round < 30; round++)
        {
            while (await router.RouteAsync(ct) > 0)
            {
            }
            await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE functions.runs SET "Status" = 'Running', "Attempts" = "Attempts" + 1
                WHERE "TenantId" = @tenant AND "Status" = 'Pending' RETURNING "Id", "EnvironmentId", "InstanceId"
                """;
            cmd.Parameters.AddWithValue("tenant", tenantId);
            var claimed = new List<(Guid, Guid, Guid)>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct)) claimed.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2)));
            }
            await using (var busy = conn.CreateCommand())
            {
                // The host's own workers run too: done is when neither of us has anything left.
                busy.CommandText = """SELECT count(*) FROM functions.runs WHERE "TenantId" = @tenant AND "Status" IN ('Pending', 'Running')""";
                busy.Parameters.AddWithValue("tenant", tenantId);
                if (claimed.Count == 0 && round > 0 && (long)(await busy.ExecuteScalarAsync(ct))! == 0)
                {
                    return;
                }
            }
            if (claimed.Count == 0)
            {
                await Task.Delay(500, ct);
            }
            foreach (var (id, environment, instance) in claimed)
            {
                await worker.ExecuteAsync(id, tenantId, environment, instance, ct);
            }
        }
    }

    private async Task<List<(string Function, string Status, string? Result, int Depth)>> RunsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "Function", "Status", "Result"::text, "Depth" FROM functions.runs WHERE "TenantId" = @tenant ORDER BY "CreatedAt" """;
        cmd.Parameters.AddWithValue("tenant", tenantId);
        var runs = new List<(string, string, string?, int)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) runs.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3)));
        return runs;
    }

    [DockerFact]
    public async Task The_assistant_writes_checks_tests_ships_and_deploys_a_function_through_its_contract()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = Host();
        var client = host.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var instanceId = await InstallAsync(client, slug, ct);
        var owner = await SeedMemberAsync(tenantId, "Owner", ct);
        var member = await SeedMemberAsync(tenantId, "Member", ct);
        Task<HttpResponseMessage> Call(string op, object body, Guid? who = null) =>
            client.SendAsync(Req(HttpMethod.Post, $"/api/admin/contracts/functions.code@1/{op}?instance=fn", slug, who ?? owner, body), ct);

        var overview = await Json(await Call("Overview", new { }), ct);
        overview.GetProperty("live").ValueKind.Should().Be(JsonValueKind.Null);
        (await Call("Overview", new { }, member)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await Json(await Call("Sdk", new { }), ct)).GetProperty("typings").GetString().Should().Contain("defineFunction");

        var written = await Json(await Call("WriteFiles", new
        {
            message = "Add greet",
            put = new Dictionary<string, string>
            {
                ["functions/greet.ts"] = """
                    import { defineFunction } from "@dcms/functions";
                    export default defineFunction({ handler: (input: { name: string }) => `Hi ${input.name}` });
                    """,
            },
        }), ct);
        written.GetProperty("branch").GetString().Should().Be("main");
        (await Call("WriteFiles", new { message = "Sneak", branch = "release", put = new Dictionary<string, string> { ["x.ts"] = "" } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "release moves only by a merge");

        var check = await Json(await Call("Check", new { }), ct);
        check.GetProperty("ok").GetBoolean().Should().BeTrue(check.ToString());
        check.GetProperty("functions").EnumerateArray().Select(f => f.GetString()).Should().Contain(["greet", "hello"]);

        var tested = await Json(await Call("Test", new { function = "greet", input = new { name = "Ada" } }), ct);
        tested.GetProperty("result").GetString().Should().Be("Hi Ada");

        var shipped = await Json(await Call("Ship", new { }), ct);
        shipped.GetProperty("merged").GetBoolean().Should().BeTrue(shipped.ToString());
        await BuildAsync(host, tenantId, instanceId, shipped.GetProperty("sha").GetString()!, ct); // the push webhook, here

        var deployed = await Json(await Call("Deploy", new { }), ct);
        deployed.GetProperty("status").GetString().Should().Be("Active");
        var live = await Json(await Call("Overview", new { }), ct);
        live.GetProperty("functions").EnumerateArray().Select(f => f.GetProperty("name").GetString()).Should().Contain("greet");
    }

    /// <summary>A Dynamic Apps app "crm": companies (with a lookup to a deal) and deals.</summary>
    private static async Task PublishCrmAsync(HttpClient client, string slug, Guid owner, CancellationToken ct)
    {
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", slug, Guid.Empty,
            new { pluginId = "dynamic-apps", slug = "crm", name = "CRM" }, superAdmin: true), ct)).EnsureSuccessStatusCode();
        var model = await Json(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/plugins/crm/_model", slug, owner), ct), ct);
        var draft = await Json(await client.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/crm/_model/draft/changes", slug, owner, new
        {
            expectedHash = model.GetProperty("hash").GetString(),
            operations = JsonDocument.Parse("""
                [{ "op": "create", "type": "table", "value": { "apiName": "companies", "displayName": "Company", "primaryFieldId": "name",
                   "fields": [ { "apiName": "name", "displayName": "Name", "required": true } ] } },
                 { "op": "create", "type": "table", "value": { "apiName": "deals", "displayName": "Deal",
                   "fields": [ { "apiName": "title", "displayName": "Title" } ] } },
                 { "op": "create", "type": "relationship", "value": { "apiName": "deal", "sourceTableId": "companies",
                   "targetTableId": "deals", "inverseApiName": "companies", "onDelete": "restrict" } }]
                """).RootElement,
        }), ct), ct);
        await Json(await client.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/crm/_model/draft/publish", slug, owner,
            new { expectedHash = draft.GetProperty("draft").GetProperty("hash").GetString() }), ct), ct);
    }

    private WebApplicationFactory<AdminApiApp::Program> Host() => fixture.Factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Forgejo:BaseUrl", _forgejo!.BaseUrl);
        builder.UseSetting("Forgejo:Token", _forgejo.Token);
        builder.UseSetting("Forgejo:WebhookUrl", "http://127.0.0.1:9/hook");
        builder.ConfigureTestServices(services => services.AddHttpClient(BuildJob.HttpClientName, c =>
            {
                c.BaseAddress = new Uri("http://script-runner");
                c.Timeout = TimeSpan.FromMinutes(3);
            })
            .ConfigurePrimaryHttpMessageHandler(() => _runner!.Server.CreateHandler()));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<FunctionSocketFactory>(_ => ct =>
                _runner!.Server.CreateWebSocketClient().ConnectAsync(new Uri(_runner.Server.BaseAddress, "session"), ct));
            services.AddHttpClient(HostCalls.EgressClientName).ConfigurePrimaryHttpMessageHandler(() => _egress);
        });
    });

    /// <summary>Writes files into the owner's draft on main, commits, merges into release; the release head.</summary>
    private static async Task<string> ShipAsync(HttpClient client, string slug, Guid owner, string at, Dictionary<string, string> files,
        string message, CancellationToken ct)
    {
        var ide = await Json(await client.SendAsync(Req(HttpMethod.Get, $"{at}/ide", slug, owner), ct), ct);
        var hashes = ide.GetProperty("hashes");
        var put = files.ToDictionary(f => f.Key, f => (object)new
        {
            content = f.Value,
            baseHash = hashes.TryGetProperty(f.Key, out var h) ? h.GetString() : null,
        });
        (await client.SendAsync(Req(HttpMethod.Patch, $"{at}/ide/files", slug, owner, new { put }), ct)).EnsureSuccessStatusCode();
        await Json(await client.SendAsync(Req(HttpMethod.Post, $"{at}/git/commit", slug, owner, new { message }), ct), ct);
        var merged = await Json(await client.SendAsync(Req(HttpMethod.Post, $"{at}/git/merge", slug, owner, new { head = "main" }), ct), ct);
        return merged.GetProperty("sha").GetString()!;
    }

    /// <summary>The build job as jobs-worker runs it, here and now rather than off the queue.</summary>
    private static async Task BuildAsync(WebApplicationFactory<AdminApiApp::Program> host, Guid tenantId, Guid instanceId, string sha, CancellationToken ct)
    {
        var ran = await host.Services.GetRequiredService<PluginHandlerRunner>().RunAsync(tenantId, "functions", instanceId, (ctx, sp) =>
            ActivatorUtilities.CreateInstance<BuildJob>(sp).RunAsync(JsonSerializer.SerializeToElement(new { sha }), ctx, ct), ct);
        ran.Should().BeTrue();
    }

    private async Task<string[]> VouchedAsync(Guid deploymentId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "VouchedPermissions" FROM functions.deployments WHERE "Id" = @id""";
        cmd.Parameters.AddWithValue("id", deploymentId);
        return (string[])(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, string slug, Guid user, object? body = null, bool superAdmin = false)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", (user == Guid.Empty ? Guid.NewGuid() : user).ToString());
        if (superAdmin) req.Headers.Add("X-Test-Roles", "SuperAdmin");
        req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        res.IsSuccessStatusCode.Should().BeTrue(text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<(Guid TenantId, string Slug)> CreateTenantAsync(HttpClient client, CancellationToken ct)
    {
        var slug = "fnd-" + Guid.NewGuid().ToString("N")[..8];
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Headers = { { "X-Test-Sub", Guid.NewGuid().ToString() }, { "X-Test-Roles", "SuperAdmin" } },
            Content = JsonContent.Create(new { slug, name = slug, ownerUserId = Guid.NewGuid(), ownerEmail = $"{slug}@dcms.test" }),
        };
        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return ((await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tenantId").GetGuid(), slug);
    }

    private static async Task<Guid> InstallAsync(HttpClient client, string slug, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", slug, Guid.Empty,
            new { pluginId = "functions", slug = "fn", name = "Functions" }, superAdmin: true), ct);
        return (await Json(res, ct)).GetProperty("id").GetGuid();
    }

    /// <summary>A member of a custom role holding exactly <paramref name="permissions"/>.</summary>
    private async Task<Guid> SeedRoleAsync(Guid tenantId, string[] permissions, CancellationToken ct)
    {
        var userId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var insert = conn.CreateCommand();
        insert.CommandText = """
            WITH role AS (
                INSERT INTO tenancy.tenant_roles ("Id", "TenantId", "EnvironmentId", "Name", "IsSystem")
                VALUES (gen_random_uuid(), @tenant, @tenant, @name, false) RETURNING "Id"),
            grants AS (
                INSERT INTO tenancy.tenant_role_permissions ("Id", "TenantId", "EnvironmentId", "TenantRoleId", "Permission")
                SELECT gen_random_uuid(), @tenant, @tenant, role."Id", p FROM role, unnest(@permissions) AS p),
            membership AS (
                INSERT INTO tenancy.tenant_memberships ("Id", "TenantId", "UserId", "Email", "CreatedAt")
                VALUES (gen_random_uuid(), @tenant, @user, @email, now()) RETURNING "Id")
            INSERT INTO tenancy.member_roles ("Id", "TenantId", "EnvironmentId", "MembershipId", "TenantRoleId")
            SELECT gen_random_uuid(), @tenant, @tenant, membership."Id", role."Id" FROM membership, role;
            """;
        insert.Parameters.AddWithValue("tenant", tenantId);
        insert.Parameters.AddWithValue("name", "Custom " + userId.ToString("N")[..6]);
        insert.Parameters.AddWithValue("permissions", permissions);
        insert.Parameters.AddWithValue("user", userId);
        insert.Parameters.AddWithValue("email", $"custom-{userId:N}@dcms.test");
        await insert.ExecuteNonQueryAsync(ct);
        return userId;
    }

    private async Task<Guid> SeedMemberAsync(Guid tenantId, string roleName, CancellationToken ct)
    {
        var userId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO tenancy.tenant_memberships ("Id", "TenantId", "UserId", "Email", "CreatedAt")
            VALUES (@id, @tenant, @user, @email, now());
            INSERT INTO tenancy.member_roles ("Id", "TenantId", "EnvironmentId", "MembershipId", "TenantRoleId")
            SELECT gen_random_uuid(), @tenant, r."EnvironmentId", @id, r."Id" FROM tenancy.tenant_roles r
            WHERE r."TenantId" = @tenant AND r."Name" = @role;
            """;
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("tenant", tenantId);
        insert.Parameters.AddWithValue("user", userId);
        insert.Parameters.AddWithValue("email", $"member-{userId:N}@dcms.test");
        insert.Parameters.AddWithValue("role", roleName);
        await insert.ExecuteNonQueryAsync(ct);
        return userId;
    }
}
