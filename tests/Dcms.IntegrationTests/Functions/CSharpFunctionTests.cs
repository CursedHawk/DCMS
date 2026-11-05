extern alias ScriptRunnerApp;

using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.Functions.Build;
using Dcms.PluginSdk.Abstractions.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using ScriptRunnerApp::Dcms.ScriptRunner;

namespace Dcms.IntegrationTests.Functions;

/// <summary>
/// C# functions in script-runner (ADR 0030): a repository's C# compiles beside its TypeScript,
/// its definitions come from the attributes, and a run executes on .NET's WebAssembly runtime in
/// the same permission-less Deno a TypeScript function gets, reaching DCMS only by host calls.
/// </summary>
public sealed class CSharpFunctionTests(DenoBinary deno, FunctionRunnerTests.Esbuild esbuild, DotnetFunctionsRuntime dotnet)
    : IClassFixture<DenoBinary>, IClassFixture<FunctionRunnerTests.Esbuild>, IClassFixture<DotnetFunctionsRuntime>
{
    private const string SdkTypes = """
        export declare function defineFunction<I, O>(spec: { handler(input: I, ctx: unknown): O | Promise<O> }): typeof spec;
        """;

    private ScriptRunnerOptions Options(Action<ScriptRunnerOptions>? configure = null)
    {
        var options = new ScriptRunnerOptions
        {
            DenoPath = deno.Path,
            EsbuildPath = esbuild.Path,
            DotnetHarnessPath = dotnet.HarnessPath,
            CSharpCompilerPath = dotnet.CompilerPath,
        };
        configure?.Invoke(options);
        return options;
    }

    private Task<BuildResult> Build(Dictionary<string, string> files, string sdk = "") =>
        new FunctionBuilder(Microsoft.Extensions.Options.Options.Create(Options()), NullLogger<FunctionBuilder>.Instance)
            .BuildAsync(new BuildRequest(files, SdkTypes, SdkCSharp: sdk), TestContext.Current.CancellationToken);

    private static string Function(string name, string body, string attributes = "", string input = "JsonNode", string output = "object") => $$"""
        namespace Tests;

        [Function("{{name}}")]
        {{attributes}}
        public sealed class F : Function<{{input}}, {{output}}>
        {
            public override async Task<{{output}}> Run({{input}} input, Context ctx)
            {
                await Task.Yield();
                {{body}}
            }
        }
        """;

    private async Task<SessionRun> Run(string code, string input = "null", string context = "{}",
        Func<string, JsonNode?, (bool Ok, JsonNode? Result, string? Error)>? broker = null, string limits = "{}", string sdk = "",
        FunctionSession? session = null, string? warm = null)
    {
        var built = await Build(new() { ["functions/F.cs"] = code }, sdk);
        built.Diagnostics.Should().BeEmpty();
        var function = built.Functions.Single();
        return await SessionDriver.RunAsync(session ?? new FunctionSession(Microsoft.Extensions.Options.Options.Create(Options()), NullLogger<FunctionSession>.Instance), new JsonObject
        {
            ["warm"] = warm,
            ["type"] = "start",
            ["runtime"] = "dotnet",
            ["assembly"] = built.Assembly,
            ["pdb"] = built.Pdb,
            ["class"] = function.Class,
            ["input"] = JsonNode.Parse(input),
            ["context"] = JsonNode.Parse(context),
            ["limits"] = JsonNode.Parse(limits),
        }, broker);
    }

    [DockerFact]
    public async Task A_mixed_repository_builds_both_languages_and_reads_the_attributes()
    {
        var built = await Build(new()
        {
            ["functions/ping.ts"] = """
                import { defineFunction } from "@dcms/functions";
                export default defineFunction({ handler: () => "pong" });
                """,
            ["functions/OrderTotal.cs"] = """
                namespace Shop;

                public record OrderInput(decimal[] Lines);

                [Function("order-total", Description = "Totals an order.")]
                [Http(Access.ApiKey)]
                [OnRow("shop.orders", RowEvent.Updated, Fields = ["status"])]
                [OnMessage("orders.paid")]
                [UsesTable("shop.orders", "read", "update")]
                [UsesSecrets("STRIPE_KEY")]
                [Limits(TimeoutMs = 5_000)]
                public sealed class OrderTotal : Function<OrderInput, decimal>
                {
                    public override Task<decimal> Run(OrderInput input, Context ctx) => Task.FromResult(Money.Sum(input.Lines));
                }
                """,
            ["lib/Money.cs"] = "namespace Shop; static class Money { public static decimal Sum(decimal[] xs) => xs.Sum(); }",
        });

        built.Diagnostics.Should().BeEmpty();
        built.Assembly.Should().NotBeNullOrEmpty();
        built.Functions.Select(f => (f.Name, f.Runtime)).Should().BeEquivalentTo([("ping", "deno-ts"), ("order-total", "dotnet")]);
        var cs = built.Functions.Single(f => f.Runtime == "dotnet");
        cs.Class.Should().Be("Shop.OrderTotal");
        cs.File.Should().Be("functions/OrderTotal.cs");
        JsonNode.DeepEquals(cs.Definition, JsonNode.Parse("""
            {
              "description": "Totals an order.",
              "triggers": [
                { "kind": "row", "table": "shop.orders", "event": "updated", "fields": ["status"] },
                { "kind": "message", "topic": "orders.paid" }
              ],
              "access": { "http": ["apiKey"] },
              "uses": { "secrets": ["STRIPE_KEY"], "tables": { "shop.orders": ["read", "update"] } },
              "limits": { "timeoutMs": 5000 }
            }
            """)).Should().BeTrue(cs.Definition!.ToJsonString());
    }

    [DockerFact]
    public async Task A_functions_folders_are_its_path_in_both_languages()
    {
        var built = await Build(new()
        {
            ["functions/orders/total.ts"] = """
                import { defineFunction } from "@dcms/functions";
                import { sum } from "../../lib/sum.ts";
                export default defineFunction({ handler: () => sum([1, 2]) });
                """,
            ["lib/sum.ts"] = "export const sum = (xs: number[]) => xs.reduce((a, b) => a + b, 0);",
            ["functions/orders/Refund.cs"] = Function("refund", "return 1;"),
            ["lib/Shared.cs"] = Function("loose", "return 2;").Replace("class F", "class G"),
        });

        built.Diagnostics.Should().BeEmpty();
        built.Functions.Select(f => f.Name).Should().BeEquivalentTo(["orders/total", "orders/refund", "loose"]);

        // A folder is part of the name, so it follows the name's rules; shared code belongs outside functions/.
        var bad = await Build(new()
        {
            ["functions/Orders/Refund.cs"] = Function("refund", "return 1;"),
            ["functions/helpers/util.ts"] = "export const x = 1;",
        });
        bad.Ok.Should().BeFalse();
        bad.Diagnostics.Should().Contain(d => d.File == "functions/Orders/Refund.cs" && d.Message.Contains("'Orders/refund'"));
        bad.Diagnostics.Should().Contain(d => d.File == "functions/helpers/util.ts" && d.Message.Contains("lib/"));
    }

    [DockerFact]
    public async Task Compile_errors_name_the_file_and_line_and_the_build_offers_only_the_curated_bcl()
    {
        var built = await Build(new()
        {
            ["functions/Broken.cs"] = Function("broken", "return undefinedThing;"),
            ["lib/Net.cs"] = "static class Net { public static object Client() => new System.Net.Http.HttpClient(); }",
            ["lib/Native.cs"] = "static class Native { [System.Runtime.InteropServices.DllImport(\"libc\")] static extern int getpid(); }",
        });

        built.Ok.Should().BeFalse();
        built.Diagnostics.Should().Contain(d => d.File == "functions/Broken.cs" && d.Line == 10 && d.Code == "CS0103");
        built.Diagnostics.Should().Contain(d => d.File == "lib/Net.cs" && d.Message.Contains("System.Net"));
        built.Diagnostics.Should().Contain(d => d.File == "lib/Native.cs");
    }

    [DockerFact]
    public async Task Two_functions_of_one_name_are_refused_across_languages()
    {
        var built = await Build(new()
        {
            ["functions/twin.ts"] = """
                import { defineFunction } from "@dcms/functions";
                export default defineFunction({ handler: () => 1 });
                """,
            ["functions/Twin.cs"] = Function("twin", "return 1;"),
        });

        built.Ok.Should().BeFalse();
        built.Diagnostics.Should().ContainSingle().Which.Message.Should().Contain("Two functions are called 'twin'");
    }

    [DockerFact]
    public async Task A_run_reaches_dcms_only_through_host_calls_the_caller_answers()
    {
        var calls = new List<(string Op, string Args)>();
        var run = await Run(Function("orders", """
                ctx.Log.Info("looking", input["min"]);
                Console.WriteLine("console is the log too");
                var page = await ctx.Table<JsonObject>("shop", "orders").Find(new Query { Filter = Filter.Gte("total", 100) });
                var key = await ctx.Secrets.Get("STRIPE_KEY");
                var refused = "";
                try { await ctx.Contract<JsonNode>("forms", "submissions", "list", new { }); } catch (HostException e) { refused = e.Message; }
                return new { count = page.Items.Count, keyLength = key?.Length, refused, method = ctx.Request?.Method };
                """),
            """{ "min": 100 }""", """{ "request": { "method": "POST", "path": "/fn/orders", "query": {}, "headers": {}, "body": null } }""",
            (op, args) =>
            {
                calls.Add((op, args!.ToJsonString()));
                return op switch
                {
                    "db" => (true, JsonNode.Parse("""{ "items": [{ "id": "1" }, { "id": "2" }], "total": 2, "page": 1, "pageSize": 50, "totalCapped": false }"""), null),
                    "secrets.get" => (true, JsonValue.Create("sk_test_123"), null),
                    _ => (false, null, "contract 'forms.submissions@1' is not in this function's uses"),
                };
            });

        run.Done["status"]!.GetValue<string>().Should().Be("ok", run.Done.ToJsonString());
        JsonNode.DeepEquals(run.Done["result"], JsonNode.Parse(
            """{"count":2,"keyLength":11,"refused":"contract 'forms.submissions@1' is not in this function's uses","method":"POST"}"""))
            .Should().BeTrue(run.Done["result"]!.ToJsonString());
        calls.Select(c => c.Op).Should().Equal("db", "secrets.get", "contract");
        calls[0].Args.Should().Be("""{"path":["shop","orders","find"],"args":[{"filter":{"field":"total","op":"gte","value":100}}]}""");
        run.Logs.Should().Equal("info: looking 100", "log: console is the log too");
    }

    [DockerTheory]
    [InlineData("return System.IO.File.ReadAllText(\"/etc/passwd\");", "/etc/passwd")]
    [InlineData("return string.Join(\",\", System.IO.Directory.GetFiles(\"/app\"));", "/app")]
    [InlineData("return Environment.GetEnvironmentVariable(\"PATH\") == \"/usr/bin:/bin\" ? \"host env\" : throw new Exception(\"no host env\");", "no host env")]
    public async Task A_csharp_run_is_as_sandboxed_as_a_typescript_one(string body, string reason)
    {
        var run = await Run(Function("probe", body));

        run.Done["status"]!.GetValue<string>().Should().Be("error", run.Done.ToJsonString());
        run.Done["error"]!.GetValue<string>().Should().Contain(reason).And.NotContain("root:");
    }

    [DockerFact]
    public async Task An_exception_names_the_file_and_line_it_was_thrown_at()
    {
        var run = await Run(Function("thrower", "throw new InvalidOperationException(\"kaput\");"));

        run.Done["status"]!.GetValue<string>().Should().Be("error");
        run.Done["error"]!.GetValue<string>().Should().StartWith("InvalidOperationException: kaput").And.Contain("in functions/F.cs:line 10");
    }

    [DockerFact]
    public async Task Limits_end_the_run_and_say_which()
    {
        (await Run(Function("loop", "while (true) { } "), limits: """{ "timeoutMs": 1500 }"""))
            .Done["error"]!.GetValue<string>().Should().Contain("longer than");

        var hog = await Run(Function("hog", "var keep = new List<byte[]>(); while (true) { keep.Add(new byte[16 * 1024 * 1024]); Console.WriteLine(keep.Count); }"));
        hog.Done["status"]!.GetValue<string>().Should().Be("limit", hog.Done.ToJsonString() + string.Join("|", hog.Logs));
        hog.Done["error"]!.GetValue<string>().Should().Contain("memory", string.Join("|", hog.Logs));
    }

    [DockerFact]
    public async Task The_generated_sdk_compiles_and_types_tables_and_contracts()
    {
        var (companies, users, stages) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var model = new SdkTypings.Model(
            [new SdkTypings.App("crm", "CRM */ </summary> \n evil \u2028 } public static class Injected { } /*\u0085x\u2029", new AppModel(
                [
                    new TableDef
                    {
                        Id = companies, ApiName = "companies", DisplayName = "Companies",
                        Fields =
                        [
                            new FieldDef { ApiName = "name", DisplayName = "Name" },
                            new FieldDef { ApiName = "employees", DisplayName = "Employees", Type = FieldType.Integer },
                            new FieldDef { ApiName = "active", DisplayName = "Active", Type = FieldType.Boolean },
                            new FieldDef { ApiName = "stage", DisplayName = "Stage", Type = FieldType.Choice, ChoiceSetId = stages },
                            new FieldDef { ApiName = "tags", DisplayName = "Tags", Type = FieldType.MultiChoice },
                            new FieldDef { ApiName = "id_card", DisplayName = "\"Quoted\" & <tagged>" },
                        ],
                    },
                    new TableDef { Id = users, ApiName = "users", DisplayName = "Users", Fields = [new FieldDef { ApiName = "email", DisplayName = "Email" }] },
                ],
                [new RelationshipDef { ApiName = "owner", SourceTableId = companies, TargetTableId = users }],
                [new ChoiceSetDef { Id = stages, ApiName = "stages", Options = [new ChoiceOption { Value = "lead" }, new ChoiceOption { Value = "won" }] }]))],
            [new SdkTypings.Instance("forms", "Forms", [(
                new ContractDescriptor("forms.submissions@1", "forms.submissions", 1, "Form submissions.",
                    [new OperationDescriptor("list", OpRisk.Read, "plugin:forms:read", OpExposure.Admin, false, "Lists them.",
                        JsonNode.Parse("""{ "type": "object", "properties": { "page": { "type": "integer" } } }""")!, null)], [], []),
                new List<OperationDescriptor>())])]);
        model.Instances[0].Contracts[0].Operations.Add(model.Instances[0].Contracts[0].Contract.Operations[0]);
        var sdk = SdkTypings.CSharp(model);
        // A name cannot leave its comment, whatever line break C# knows it by.
        sdk.Should().NotContainAny("\u2028", "\u2029", "\u0085");

        var calls = new List<string>();
        var run = await Run(Function("crm", """
                var created = await ctx.Db.Crm.Companies.Create(new CrmCompaniesRow { Name = "Acme", Employees = 12, Active = true, Tags = ["b2b"] });
                var owner = created.Owner;
                await ctx.Plugins.Forms.FormsSubmissions.List(new { page = 2 });
                return new { created.Id, created.Name, created.Employees, created.Version, owner, card = created.IdCard };
                """, """[UsesTable("crm.companies", "create")] [UsesContracts("forms.submissions@1")]"""), sdk: sdk, broker: (op, args) =>
            {
                calls.Add(args!.ToJsonString());
                return op == "db"
                    ? (true, JsonNode.Parse("""{ "id": "c1", "version": 1, "name": "Acme", "employees": 12, "owner": "u1", "id_card": "X-1" }"""), null)
                    : (true, JsonNode.Parse("[]"), null);
            });

        run.Done["status"]!.GetValue<string>().Should().Be("ok", run.Done.ToJsonString() + "\n" + sdk);

        // The build compiles only what is declared: an undeclared table is a compile error saying so.
        var undeclared = await Build(new() { ["functions/F.cs"] = Function("users", "return await ctx.Db.Crm.Users.Count();") }, sdk);
        undeclared.Ok.Should().BeFalse();
        undeclared.Diagnostics.Should().Contain(d => d.Code == "CS1061" && d.Message.Contains("[UsesTable(\"app.table\""));
        calls.Should().Equal(
            """{"path":["crm","companies","create"],"args":[{"name":"Acme","employees":12,"active":true,"tags":["b2b"]}]}""",
            """{"path":["forms","forms.submissions","list"],"args":[{"page":2}]}""");
        JsonNode.DeepEquals(run.Done["result"], JsonNode.Parse("""{"id":"c1","name":"Acme","employees":12,"version":1,"owner":"u1","card":"X-1"}"""))
            .Should().BeTrue(run.Done["result"]!.ToJsonString());
    }

    [DockerFact]
    public async Task A_deployment_keeps_its_process_warm_and_only_for_itself()
    {
        // Statics survive between runs of one kept process: the counter says which process ran.
        const string counter = """
            namespace Tests;
            [Function("count")]
            public sealed class F : Function<JsonNode, int>
            {
                static int s_runs;
                public override Task<int> Run(JsonNode input, Context ctx) => Task.FromResult(++s_runs);
            }
            """;
        using var session = new FunctionSession(Microsoft.Extensions.Options.Options.Create(Options()), NullLogger<FunctionSession>.Instance);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var first = await Run(counter, session: session, warm: "tenant-a:deployment-1");
        var cold = clock.Elapsed;
        clock.Restart();
        var second = await Run(counter, session: session, warm: "tenant-a:deployment-1");
        var warm = clock.Elapsed;
        var other = await Run(counter, session: session, warm: "tenant-b:deployment-9");
        var unkept = await Run(counter, session: session);

        first.Done["result"]!.GetValue<int>().Should().Be(1);
        second.Done["result"]!.GetValue<int>().Should().Be(2, "the deployment's kept process ran it");
        other.Done["result"]!.GetValue<int>().Should().Be(1, "another deployment never gets that process");
        unkept.Done["result"]!.GetValue<int>().Should().Be(1, "a run without a key is never kept");
        // The build in Run() is most of the wall time; the session part of a warm run drops below the cold one.
        TestContext.Current.SendDiagnosticMessage($"cold {cold.TotalMilliseconds:0} ms, warm {warm.TotalMilliseconds:0} ms");
    }

    [DockerFact]
    public async Task A_kept_process_that_hits_a_limit_is_not_kept()
    {
        const string hog = """
            namespace Tests;
            [Function("hog")]
            public sealed class F : Function<JsonNode, int>
            {
                static int s_runs;
                public override Task<int> Run(JsonNode input, Context ctx)
                {
                    if (++s_runs == 1) { var keep = new List<byte[]>(); while (true) keep.Add(new byte[16 * 1024 * 1024]); }
                    return Task.FromResult(s_runs);
                }
            }
            """;
        using var session = new FunctionSession(Microsoft.Extensions.Options.Options.Create(Options()), NullLogger<FunctionSession>.Instance);
        (await Run(hog, session: session, warm: "t:d")).Done["status"]!.GetValue<string>().Should().Be("limit");
        (await Run(hog, session: session, warm: "t:d")).Done["status"]!.GetValue<string>().Should().Be("limit", "a fresh process starts over");
    }

    [DockerFact]
    public async Task A_kept_process_busy_after_its_run_is_not_reused()
    {
        // Work left running after the answer would escape the run's time limit: it costs the process.
        const string spinner = """
            namespace Tests;
            [Function("spin")]
            public sealed class F : Function<JsonNode, int>
            {
                static int s_runs;
                public override Task<int> Run(JsonNode input, Context ctx)
                {
                    if (++s_runs == 1) _ = Task.Run(() => { while (true) { } });
                    return Task.FromResult(s_runs);
                }
            }
            """;
        using var session = new FunctionSession(Microsoft.Extensions.Options.Options.Create(Options()), NullLogger<FunctionSession>.Instance);
        (await Run(spinner, session: session, warm: "t:spin")).Done["result"]!.GetValue<int>().Should().Be(1);
        await Task.Delay(1_000, TestContext.Current.CancellationToken);
        (await Run(spinner, session: session, warm: "t:spin")).Done["result"]!.GetValue<int>().Should().Be(1, "the busy process was retired, not reused");
    }
}
