extern alias AdminApiApp;
extern alias ScriptRunnerApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Plugins.Functions.Build;
using Dcms.Plugins.Functions.Runtime;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Data.Rls;
using Dcms.PluginSdk.Runtime.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Npgsql;

namespace Dcms.IntegrationTests.Functions;

/// <summary>
/// Build and deploy (ADR 0029, F3), against a real Forgejo and the real script-runner: code
/// written through the IDE's routes and merged into release builds into a deployment, which
/// goes live only when someone holding what it uses deploys it; a broken build says where.
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class FunctionDeployTests(AdminApiFixture fixture, DenoBinary deno, FunctionRunnerTests.Esbuild esbuild, DotnetFunctionsRuntime dotnet)
    : IClassFixture<DenoBinary>, IClassFixture<FunctionRunnerTests.Esbuild>, IClassFixture<DotnetFunctionsRuntime>, IAsyncLifetime
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
            b.UseSetting("ScriptRunner:DotnetHarnessPath", dotnet.HarnessPath);
            b.UseSetting("ScriptRunner:CSharpCompilerPath", dotnet.CompilerPath);
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
            "plugin:dynamic-apps:data-read", "plugin:dynamic-apps:data-write", "plugin:functions:secrets", "plugin:functions:deploy",
            "plugin:functions:egress");
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
    public async Task A_repository_mixes_typescript_and_csharp_and_one_invokes_the_other()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = Host();
        var client = host.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        await InstallAsync(client, slug, ct);
        var owner = await SeedMemberAsync(tenantId, "Owner", ct);
        var api = "/api/admin/plugins/fn/_functions";

        // The C# half of the SDK is served beside the TypeScript one (ADR 0030).
        var sdk = await client.SendAsync(Req(HttpMethod.Get, $"{api}/sdk.cs", slug, owner), ct);
        sdk.StatusCode.Should().Be(HttpStatusCode.OK);
        (await sdk.Content.ReadAsStringAsync(ct)).Should().Contain("public sealed class Database(Context ctx)");

        var files = new Dictionary<string, string>
        {
            ["functions/caller.ts"] = """
                import { defineFunction } from "@dcms/functions";
                export default defineFunction({
                  async handler(input: { lines: number[] }, ctx) {
                    const total = await ctx.functions.invoke<number>("order-total", input);
                    return { total, from: "ts" };
                  },
                });
                """,
            ["functions/thrower.ts"] = """
                import { defineFunction } from "@dcms/functions";
                import { fail } from "../lib/fail.ts";
                export default defineFunction({ handler: () => fail("kaput") });
                """,
            ["lib/fail.ts"] = """
                export function fail(why: string): never {
                  throw new Error(why);
                }
                """,
            ["functions/WhoCalls.cs"] = """
                namespace Shop;
                [Function("who-calls")]
                public sealed class WhoCalls : Function<JsonNode, object>
                {
                    public override Task<object> Run(JsonNode input, Context ctx) =>
                        Task.FromResult<object>(new { method = ctx.Request?.Method, user = ctx.User?.Email, trigger = ctx.Trigger?.Kind });
                }
                """,
            ["functions/OrderTotal.cs"] = """
                namespace Shop;
                public record Order(decimal[] Lines);

                [Function("order-total")]
                public sealed class OrderTotal : Function<Order, decimal>
                {
                    public override Task<decimal> Run(Order input, Context ctx) => Task.FromResult(input.Lines.Sum());
                }
                """,
        };
        var check = await Json(await client.SendAsync(Req(HttpMethod.Post, $"{api}/check", slug, owner, new { files }), ct), ct);
        check.GetProperty("ok").GetBoolean().Should().BeTrue(check.ToString());
        check.GetProperty("functions").EnumerateArray().Select(f => f.GetString()).Should().BeEquivalentTo(["caller", "order-total", "thrower", "who-calls"]);
        // What the editor's Functions panel lists: each function's runtime and file.
        check.GetProperty("specs").EnumerateArray()
            .Select(f => (f.GetProperty("name").GetString(), f.GetProperty("runtime").GetString(), f.GetProperty("file").GetString()))
            .Should().Contain([("caller", "deno-ts", "functions/caller.ts"), ("order-total", "dotnet", "functions/OrderTotal.cs")]);

        var run = await Json(await client.SendAsync(Req(HttpMethod.Post, $"{api}/test", slug, owner,
            new { files, function = "caller", input = new { lines = new[] { 1.5, 2.5, 6 } } }), ct), ct);
        run.GetProperty("status").GetString().Should().Be("ok", run.ToString());
        run.GetProperty("result").GetProperty("total").GetDecimal().Should().Be(10m);
        // The trace: the TypeScript function's one host call, the C# one it invoked (workbench W3).
        run.GetProperty("trace").EnumerateArray().Select(e => e.GetProperty("op").GetString()).Should().Equal("functions.invoke");
        run.GetProperty("trace")[0].GetProperty("ok").GetBoolean().Should().BeTrue();

        // A TypeScript error names the repository's file and line, not the bundle's (W3).
        var thrown = await Json(await client.SendAsync(Req(HttpMethod.Post, $"{api}/test", slug, owner, new { files, function = "thrower" }), ct), ct);
        thrown.GetProperty("status").GetString().Should().Be("error");
        thrown.GetProperty("error").GetString().Should().Contain("kaput").And.Contain("lib/fail.ts:2:");

        // A simulated HTTP call reaches the function's context (W2); the run is still a manual one.
        var simulated = await Json(await client.SendAsync(Req(HttpMethod.Post, $"{api}/test", slug, owner, new
        {
            files,
            function = "who-calls",
            simulate = new
            {
                trigger = new { kind = "http", detail = new { method = "POST", path = "/fn/who-calls" } },
                request = new { method = "POST", path = "/fn/who-calls", query = new { }, headers = new { }, body = (object?)null },
                user = new { id = Guid.NewGuid(), email = "ada@example.com" },
            },
        }), ct), ct);
        simulated.GetProperty("status").GetString().Should().Be("ok", simulated.ToString());
        simulated.GetProperty("result").ToString().Should().Contain("\"method\":\"POST\"").And.Contain("ada@example.com").And.Contain("\"trigger\":\"http\"");
        (await client.SendAsync(Req(HttpMethod.Post, $"{api}/test", slug, owner, new { files, function = "who-calls", simulate = new { trigger = new { kind = "admin" } } }), ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
                    // The global fetch, a secret the function never holds, bytes both ways, and redirects.
                    const viaRef = await (await fetch("https://api.example.com/ping", { headers: { authorization: "Bearer " + ctx.secrets.ref("STRIPE_KEY") } })).text();
                    const echoed = new Uint8Array(await (await fetch("https://api.example.com/bytes", { method: "POST", body: new Uint8Array([0, 255, 7]) })).arrayBuffer());
                    const redirected = await fetch("https://api.example.com/redirect");
                    const posted = await ctx.http.fetch("https://api.example.com/json", { method: "POST", body: { a: 1 } });
                    const doubled = await ctx.functions.invoke<number>("double", { n: count });
                    const job = await ctx.jobs.enqueue("double", { n: 1 }, { dedupeKey: "once" });
                    const viaContract = await ctx.plugins.crm.dynamicAppsRecords.getRecord({ table: "companies", id: created.id });
                    const refusals: string[] = [];
                    for (const attempt of [() => db.crm.deals.find(), () => db.crm.companies.delete(created.id),
                                           () => ctx.http.fetch("https://evil.example.org/"), () => ctx.secrets.get("OTHER"),
                                           () => fetch("https://api.example.com/away"),
                                           () => fetch("https://api.example.com/ping", { headers: { authorization: ctx.secrets.ref("OTHER") } }),
                                           () => db.crm.companies.find({ expand: ["deal"] }),
                                           () => db.crm.companies.count({ field: "deal.title", op: "eq", value: "x" }),
                                           () => ctx.plugins.crm.dynamicAppsConfig.getSummary({})]) {
                      try { await attempt(); refusals.push("allowed"); } catch (e) { refusals.push((e as Error).message); }
                    }
                    ctx.log.info("created", created.name);
                    return { name: created.name, count, status: reply.status, pong: await reply.text(), viaRef, echoed: [...echoed],
                             redirected: [redirected.status, await redirected.text()], posted: await posted.text(), deno: typeof (globalThis as any).Deno,
                             doubled, job: job.id, viaContract: viaContract?.name ?? null, refusals };
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
        // Sending requests out is what deploying it takes too.
        (await Json(await client.SendAsync(Req(HttpMethod.Get, $"{api}/deployments/{builtId}", slug, owner), ct), ct))
            .GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().Contain("plugin:functions:egress");

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
        refusals[4].Should().Contain("uses.egress: [\"evil.example.org\"]", "a redirect goes only where the function may");
        refusals[5].Should().Contain("uses.secrets: [\"OTHER\"]", "a secret ref is a secret like any other");
        refusals[6].Should().Contain("Following companies.deal reads deals", "a lookup leads into another table");
        refusals[7].Should().Contain("Following companies.deal reads deals");
        refusals[8].Should().Contain("uses.contracts: [\"dynamic-apps.config@1\"]");
        result.GetProperty("viaRef").GetString().Should().Be("pong for Bearer sk_test_1", "the platform put the secret in");
        result.GetProperty("echoed").EnumerateArray().Select(b => b.GetInt32()).Should().Equal(7, 255, 0);
        result.GetProperty("redirected").EnumerateArray().Select(v => v.ToString()).Should().Equal("200", "pong for ");
        result.GetProperty("posted").GetString().Should().Be("application/json {\"a\":1}");
        result.GetProperty("deno").GetString().Should().Be("undefined");
        run.GetProperty("trace").ToString().Should().NotContain("sk_test_1", "a secret ref never enters the trace");
        result.GetProperty("viaContract").GetString().Should().Be("Acme", "a declared contract runs with the vouched permissions");
        run.GetProperty("logs").GetString().Should().Contain("[info] created Acme");
        _egress.Requests.Should().Equal(
            "https://api.example.com/ping", "https://api.example.com/ping", "https://api.example.com/bytes",
            "https://api.example.com/redirect", "https://api.example.com/ping", "https://api.example.com/json", "https://api.example.com/away");

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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            return request.RequestUri.AbsolutePath switch
            {
                "/bytes" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([.. body.Reverse()]) },
                "/redirect" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/ping", UriKind.Relative) } },
                "/away" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example.org/x") } },
                "/json" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{request.Content!.Headers.ContentType} {System.Text.Encoding.UTF8.GetString(body)}"),
                },
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"pong for {request.Headers.Authorization}") },
            };
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
        // Whoever takes the slot -- this, or the collection's shared host ticking too -- it starts once.
        await worker.ScheduleAsync(now, ct);
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
                // Other hosts' workers run too: done is when nobody has anything left.
                // An event not yet routed is work too: the collection's shared host may hold it mid-route.
                busy.CommandText = """
                    SELECT (SELECT count(*) FROM functions.runs WHERE "TenantId" = @tenant AND "Status" IN ('Pending', 'Running'))
                         + (SELECT count(*) FROM apps.outbox WHERE "TenantId" = @tenant AND "SentAt" IS NULL AND "Attempts" < 10)
                    """;
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
        (await Call("WriteFiles", new { message = "Escape", put = new Dictionary<string, string> { ["../../etc/x.ts"] = "" } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "a path outside the repository is refused");

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

    [DockerFact]
    public async Task What_is_live_deploys_to_another_environment_built_there_and_vouched_for_by_its_deployer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = Host();
        var client = host.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var instanceId = await InstallAsync(client, slug, ct);
        var owner = await SeedMemberAsync(tenantId, "Owner", ct);
        var api = "/api/admin/plugins/fn/_functions";

        var repo = await Json(await client.SendAsync(Req(HttpMethod.Get, $"{api}/repo", slug, owner), ct), ct);
        var sha = await ShipAsync(client, slug, owner, $"/api/admin/repos/{repo.GetProperty("id").GetGuid()}", new()
        {
            ["functions/greet.ts"] = """
                import { defineFunction } from "@dcms/functions";
                export default defineFunction({ uses: { secrets: ["GREETING"] }, handler: (input: { name: string }) => `Hi ${input.name}` });
                """,
        }, "Greet", ct);
        await BuildAsync(host, tenantId, instanceId, sha, ct);
        var built = (await Json(await client.SendAsync(Req(HttpMethod.Get, $"{api}/deployments", slug, owner), ct), ct)).EnumerateArray().First();
        await Json(await client.SendAsync(Req(HttpMethod.Post, $"{api}/deployments/{built.GetProperty("id").GetGuid()}/activate", slug, owner), ct), ct);

        // A second environment with the app installed and nothing deployed.
        var staging = (await Json(await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", slug, owner, new { name = "staging", empty = true }), ct), ct))
            .GetProperty("id").GetGuid();
        var stagingInstance = await InstallAsync(client, $"{slug}/staging", ct);

        async Task<IReadOnlyList<EnvironmentConfigItem>> ExportAsync(Guid environment)
        {
            using var scope = host.Services.CreateScope();
            using var rls = RlsScope.Environment(tenantId, environment);
            return await Kind(scope).ExportAsync(tenantId, ct);
        }
        async Task ApplyAsync(JsonElement value, IReadOnlySet<string>? held)
        {
            using var scope = host.Services.CreateScope();
            using var rls = RlsScope.Environment(tenantId, staging);
            await Kind(scope).ApplyAsync(tenantId, "fn", value, new EnvironmentApplyContext(held), ct);
        }
        static IEnvironmentConfigKind Kind(IServiceScope scope) =>
            scope.ServiceProvider.GetServices<IEnvironmentConfigKind>().Single(k => k.Kind == "functions");

        var item = (await ExportAsync(tenantId)).Should().ContainSingle().Subject;
        item.Key.Should().Be("fn");
        item.Value.GetProperty("Live").GetString().Should().Be(sha);
        item.Value.GetProperty("Permissions").EnumerateArray().Select(p => p.GetString()).Should().Contain("plugin:functions:secrets");
        (await ExportAsync(staging)).Should().BeEmpty("nothing is live there");

        // A deployer who lacks what it uses there is refused: built, but not live.
        var refused = () => ApplyAsync(item.Value, new HashSet<string> { "plugin:functions:deploy" });
        (await refused.Should().ThrowAsync<EnvironmentConfigException>()).Which.Message.Should().Contain("plugin:functions:secrets");
        (await StatusesAsync(stagingInstance, ct)).Should().Equal("Ready");

        await ApplyAsync(item.Value, held: null);
        await ApplyAsync(item.Value, held: null); // again, as an interrupted deployment is: nothing new
        (await StatusesAsync(stagingInstance, ct)).Should().Equal("Active");
        var arrived = (await ExportAsync(staging)).Should().ContainSingle().Subject;
        arrived.Value.GetProperty("Live").GetString().Should().NotBe(sha, "the same files, as this environment's own commit");
        arrived.Value.GetProperty("Permissions").GetRawText().Should().Be(item.Value.GetProperty("Permissions").GetRawText());

        // The code arrived on release and on the branch the editor opens.
        await host.Services.GetRequiredService<PluginHandlerRunner>().RunAsync(tenantId, "functions", stagingInstance, async (ctx, _) =>
        {
            var repos = ctx.Contracts.Get<IPluginRepos>();
            foreach (var branch in new[] { "release", "main" })
            {
                (await repos.ReadFilesAsync(new RepoFilesQuery(branch), ct)).Files.Should().ContainKey("functions/greet.ts", branch);
            }
        }, ct);

        // Where deployments manage the app, nothing goes live by hand.
        (await client.SendAsync(Req(HttpMethod.Put, $"/api/admin/environments/{tenantId}/policies", slug, owner,
            new[] { new { kind = "functions", itemKey = (string?)null, mode = "managed" } }), ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.SendAsync(Req(HttpMethod.Post, $"{api}/deployments/{built.GetProperty("id").GetGuid()}/activate", slug, owner), ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private async Task<List<string>> StatusesAsync(Guid instanceId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "Status" FROM functions.deployments WHERE "InstanceId" = @instance ORDER BY "Number" """;
        cmd.Parameters.AddWithValue("instance", instanceId);
        var statuses = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) statuses.Add(reader.GetString(0));
        return statuses;
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
            // These tests route the apps' events, run the queue and tick schedules themselves; the
            // host's own workers doing it too raced them: a run or an event in another's hands when
            // the drain looked made it end early, and a tick taken first left the test none.
            foreach (var worker in new[] { typeof(RunWorker), typeof(Dcms.Plugins.DynamicApps.Automation.AutomationWorker) })
            {
                services.Remove(services.Single(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                                                     && d.ImplementationType == worker));
            }
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
        // "{tenant}/{environment}" names an environment other than production.
        var parts = slug.Split('/', 2);
        req.Headers.Add("X-Dcms-Tenant", parts[0]);
        if (parts.Length > 1) req.Headers.Add("X-Dcms-Environment", parts[1]);
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
