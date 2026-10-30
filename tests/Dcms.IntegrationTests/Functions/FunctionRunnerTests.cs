extern alias ScriptRunnerApp;

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Dcms.IntegrationTests.Automation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScriptRunnerApp::Dcms.ScriptRunner;

namespace Dcms.IntegrationTests.Functions;

/// <summary>
/// script-runner's Functions side (ADR 0029), driving the Deno the image pins and the repo's own
/// esbuild: a repository builds into bundles, a bundle runs over a session whose host calls the
/// test answers as a broker would, and every way out — of the sandbox, of the repository, past a
/// limit — is refused.
/// </summary>
public sealed class FunctionRunnerTests(ScriptSandboxTests.Deno deno, FunctionRunnerTests.Esbuild esbuild)
    : IClassFixture<ScriptSandboxTests.Deno>, IClassFixture<FunctionRunnerTests.Esbuild>
{
    /// <summary>
    /// The esbuild binary of the version script-runner's Dockerfile pins: the repo's pnpm copy when
    /// there is one, else that same npm tarball, checked against the same checksum.
    /// </summary>
    public sealed class Esbuild : IAsyncLifetime
    {
        // Keep in step with src/Services/Dcms.ScriptRunner/Dockerfile (ESBUILD_VERSION, ESBUILD_SHA256).
        private const string Version = "0.28.2";
        private const string Sha256 = "9573bb2233aab0f9ea7647d5cca9726113cc1768de61d66b17267f4db84488f6";

        public string Path { get; private set; } = "esbuild";
        private string? _downloaded;

        public async ValueTask InitializeAsync()
        {
            if (!DockerFactAttribute.Available || !OperatingSystem.IsLinux())
            {
                return;
            }
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "Dcms.sln")))
            {
                directory = directory.Parent;
            }
            var local = System.IO.Path.Combine(directory?.FullName ?? ".", "node_modules", ".pnpm",
                $"@esbuild+linux-x64@{Version}", "node_modules", "@esbuild", "linux-x64", "bin", "esbuild");
            if (File.Exists(local))
            {
                Path = local;
                return;
            }

            using var http = new HttpClient();
            var tarball = await http.GetByteArrayAsync($"https://registry.npmjs.org/@esbuild/linux-x64/-/linux-x64-{Version}.tgz");
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(tarball)).Should().Be(Sha256);
            await using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(tarball), System.IO.Compression.CompressionMode.Decompress);
            await using var tar = new System.Formats.Tar.TarReader(gzip);
            while (await tar.GetNextEntryAsync() is { } entry)
            {
                if (entry.Name == "package/bin/esbuild")
                {
                    _downloaded = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"esbuild-{Guid.NewGuid():N}");
                    await entry.ExtractToFileAsync(_downloaded, overwrite: false);
                    File.SetUnixFileMode(_downloaded, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    Path = _downloaded;
                    return;
                }
            }
            throw new InvalidOperationException("The esbuild tarball holds no package/bin/esbuild.");
        }

        public ValueTask DisposeAsync()
        {
            if (_downloaded is not null && File.Exists(_downloaded))
            {
                File.Delete(_downloaded);
            }
            return ValueTask.CompletedTask;
        }
    }

    private const string SdkTypes = """
        export interface Ctx {
          db: any;
          plugins: any;
          secrets: { get(name: string): Promise<string | null> };
          log: Console;
          request: { method: string; body: unknown } | null;
        }
        export declare function defineFunction<I, O>(spec: {
          triggers?: unknown[]; access?: unknown; uses?: unknown;
          handler(input: I, ctx: Ctx): O | Promise<O>;
        }): typeof spec;
        export declare const on: {
          row(table: string): { created(o?: object): unknown; updated(o?: object): unknown; deleted(o?: object): unknown };
          message(topic: string): unknown;
        };
        """;

    private ScriptRunnerOptions Options(Action<ScriptRunnerOptions>? configure = null)
    {
        var options = new ScriptRunnerOptions { DenoPath = deno.Path, EsbuildPath = esbuild.Path };
        configure?.Invoke(options);
        return options;
    }

    private Task<BuildResult> Build(Dictionary<string, string> files, Action<ScriptRunnerOptions>? configure = null) =>
        new FunctionBuilder(Microsoft.Extensions.Options.Options.Create(Options(configure)), NullLogger<FunctionBuilder>.Instance)
            .BuildAsync(new BuildRequest(files, SdkTypes), TestContext.Current.CancellationToken);

    private async Task<string> BundleOf(string code)
    {
        var built = await Build(new() { ["functions/f.ts"] = code });
        built.Diagnostics.Should().BeEmpty();
        return built.Functions.Single().Bundle;
    }

    [DockerFact]
    public async Task A_repository_builds_into_one_bundle_per_function_with_its_definition()
    {
        var built = await Build(new()
        {
            ["functions/order-paid.ts"] = """
                import { defineFunction, on } from "@dcms/functions";
                import { total } from "../lib/money.ts";
                export default defineFunction({
                  triggers: [on.row("shop.orders").updated({ fields: ["status"] }), on.message("orders.paid")],
                  access: { http: ["apiKey"] },
                  uses: { tables: { "shop.orders": ["read", "update"] } },
                  async handler(input: { lines: number[] }) { return total(input.lines); },
                });
                """,
            ["functions/hello.ts"] = """
                import { defineFunction } from "@dcms/functions";
                export default defineFunction({ handler: (input: { name: string }) => `hi ${input.name}` });
                """,
            ["lib/money.ts"] = "export const total = (xs: number[]): number => xs.reduce((a, b) => a + b, 0);",
            ["README.md"] = "# not code",
        });

        built.Diagnostics.Should().BeEmpty();
        built.Ok.Should().BeTrue();
        built.Functions.Select(f => f.Name).Should().Equal("hello", "order-paid");
        var paid = built.Functions.Single(f => f.Name == "order-paid");
        paid.Definition!["triggers"]!.ToJsonString().Should().Be(
            """[{"kind":"row","table":"shop.orders","event":"updated","fields":["status"]},{"kind":"message","topic":"orders.paid"}]""");
        paid.Definition["access"]!.ToJsonString().Should().Be("""{"http":["apiKey"]}""");
        paid.Definition.AsObject().ContainsKey("handler").Should().BeFalse();
        paid.Bundle.Should().Contain("reduce").And.NotContain("@dcms/functions\"");
    }

    [DockerFact]
    public async Task A_type_error_names_its_file_and_line()
    {
        var built = await Build(new()
        {
            ["functions/bad.ts"] = """
                import { defineFunction } from "@dcms/functions";
                export default defineFunction({
                  async handler(input: { n: number }, ctx) {
                    const s: string = input.n;
                    return s;
                  },
                });
                """,
        });

        built.Ok.Should().BeFalse();
        var problem = built.Diagnostics.Should().ContainSingle().Subject;
        problem.File.Should().Be("functions/bad.ts");
        problem.Line.Should().Be(4);
        problem.Code.Should().Be("TS2322");
        built.Diagnostics.Should().AllSatisfy(d => d.Message.Should().NotContain("/tmp/"));
    }

    [DockerTheory]
    [InlineData("import x from '/etc/hostname';")]
    [InlineData("import x from 'file:///etc/passwd';")]
    [InlineData("import x from '../../../../etc/hostname';")]
    [InlineData("import x from 'npm:left-pad';")]
    [InlineData("import x from 'https://deno.land/std/version.ts';")]
    [InlineData("export { default } from 'node:fs';")]
    [InlineData("const x = await import('/etc/hostname');")]
    public async Task A_function_imports_only_its_repository_and_the_sdk(string import)
    {
        var built = await Build(new()
        {
            ["functions/evil.ts"] = import + "\nexport const y = 1;\n",
        });

        built.Ok.Should().BeFalse();
        built.Functions.Should().BeEmpty();
        built.Diagnostics.Should().Contain(d => d.File == "functions/evil.ts" && d.Line == 1);
        built.Diagnostics.Should().AllSatisfy(d => d.Message.Should().NotContain("root:"));
    }

    [DockerTheory]
    [InlineData("../outside.ts")]
    [InlineData("/abs.ts")]
    [InlineData(".dcms/functions.ts")]
    [InlineData("functions/../../x.ts")]
    public async Task A_path_outside_the_repository_is_refused(string path)
    {
        var built = await Build(new() { [path] = "export {}", ["functions/a.ts"] = "export {}" });
        built.Ok.Should().BeFalse();
        built.Diagnostics.Should().Contain(d => d.File == path);
    }

    [DockerFact]
    public async Task A_function_without_define_function_says_so()
    {
        var built = await Build(new() { ["functions/plain.ts"] = "export default function main() { return 1 }" });
        built.Ok.Should().BeFalse();
        built.Diagnostics.Single().Message.Should().Contain("defineFunction");
    }

    [DockerFact]
    public async Task A_run_reaches_dcms_only_through_host_calls_the_caller_answers()
    {
        var bundle = await BundleOf("""
            import { defineFunction } from "@dcms/functions";
            export default defineFunction({
              async handler(input: { min: number }, ctx) {
                ctx.log.info("looking", input.min);
                const page = await ctx.db.shop.orders.find({ where: { total: { gte: input.min } } });
                const key = await ctx.secrets.get("STRIPE_KEY");
                let refused = "";
                try { await ctx.plugins.forms.submissions.list({}); } catch (e) { refused = (e as Error).message; }
                return { count: page.items.length, keyLength: key?.length, refused, method: ctx.request?.method };
              },
            });
            """);

        var calls = new List<(string Op, string Args)>();
        var run = await Session(bundle, """{ "min": 100 }""", """{ "request": { "method": "POST", "body": null } }""",
            (op, args) =>
            {
                calls.Add((op, args!.ToJsonString()));
                return op switch
                {
                    "db" => (true, JsonNode.Parse("""{ "items": [{ "id": 1 }, { "id": 2 }] }"""), null),
                    "secrets.get" => (true, JsonValue.Create("sk_test_123"), null),
                    _ => (false, null, "contract 'forms.submissions@1' is not in this function's uses"),
                };
            });

        run.Done["status"]!.GetValue<string>().Should().Be("ok", run.Done.ToJsonString());
        JsonNode.DeepEquals(run.Done["result"], JsonNode.Parse(
            """{"count":2,"keyLength":11,"refused":"contract 'forms.submissions@1' is not in this function's uses","method":"POST"}"""))
            .Should().BeTrue(run.Done["result"]!.ToJsonString());
        calls.Select(c => c.Op).Should().Equal("db", "secrets.get", "contract");
        calls[0].Args.Should().Be("""{"path":["shop","orders","find"],"args":[{"where":{"total":{"gte":100}}}]}""");
        run.Logs.Should().ContainSingle().Which.Should().Be("info: looking 100");
    }

    [DockerTheory]
    [InlineData("return Deno.readTextFileSync('/etc/hostname')", "read access")]
    [InlineData("return Deno.env.toObject()", "env access")]
    [InlineData("return (await fetch('http://169.254.169.254/')).status", "net access")]
    [InlineData("return await new Deno.Command('/bin/sh').output()", "run access")]
    // A literal specifier is refused at build time (above); a computed one only the sandbox can stop.
    [InlineData("const p = ['file:///etc', 'passwd'].join('/'); return await import(p)", "read access")]
    public async Task A_function_run_is_as_sandboxed_as_a_flow_script(string body, string reason)
    {
        var bundle = await BundleOf($$"""
            import { defineFunction } from "@dcms/functions";
            export default defineFunction({ async handler() { {{body}} } });
            """);

        var run = await Session(bundle);

        run.Done["status"]!.GetValue<string>().Should().Be("error");
        run.Done["error"]!.GetValue<string>().Should().Contain(reason).And.NotContain("root:");
    }

    [DockerFact]
    public async Task Limits_end_the_run_and_say_which()
    {
        var loop = await BundleOf("""
            import { defineFunction } from "@dcms/functions";
            export default defineFunction({ handler() { while (true) {} } });
            """);
        var chatty = await BundleOf("""
            import { defineFunction } from "@dcms/functions";
            export default defineFunction({ async handler(_: unknown, ctx) { for (let i = 0; i < 50; i++) await ctx.secrets.get("x"); } });
            """);
        var big = await BundleOf("""
            import { defineFunction } from "@dcms/functions";
            export default defineFunction({ handler: () => "x".repeat(200_000) });
            """);
        var noisy = await BundleOf("""
            import { defineFunction } from "@dcms/functions";
            export default defineFunction({ handler(_: unknown, ctx) { for (let i = 0; i < 2_000; i++) ctx.log.log("x".repeat(100)); return 1; } });
            """);

        (await Session(loop, limits: """{ "timeoutMs": 500 }""")).Done["error"]!.GetValue<string>().Should().Contain("longer than");
        (await Session(chatty, broker: (_, _) => (true, null, null), configure: o => o.MaxCalls = 10)).Done["error"]!.GetValue<string>().Should().Contain("at most 10 host calls");
        (await Session(big, configure: o => o.MaxResultChars = 100_000)).Done["error"]!.GetValue<string>().Should().Contain("result is at most");

        var logged = await Session(noisy, configure: o => o.MaxLogChars = 1_000);
        logged.Done["status"]!.GetValue<string>().Should().Be("ok");
        string.Concat(logged.Logs).Length.Should().BeLessThan(1_200);
        logged.Logs.Last().Should().Contain("the log is cut");
    }

    private sealed record SessionRun(JsonObject Done, List<string> Logs);

    private async Task<SessionRun> Session(string bundle, string input = "null", string context = "{}",
        Func<string, JsonNode?, (bool Ok, JsonNode? Result, string? Error)>? broker = null,
        string limits = "{}", Action<ScriptRunnerOptions>? configure = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var session = new FunctionSession(Microsoft.Extensions.Options.Options.Create(Options(configure)), NullLogger<FunctionSession>.Instance);
        var (server, client) = await PairAsync();
        using (server)
        using (client)
        {
            var serving = session.RunAsync(server, ct);
            await Send(client, new JsonObject
            {
                ["type"] = "start",
                ["mode"] = "run",
                ["bundle"] = bundle,
                ["input"] = JsonNode.Parse(input),
                ["context"] = JsonNode.Parse(context),
                ["limits"] = JsonNode.Parse(limits),
            }, ct);

            var logs = new List<string>();
            JsonObject? done = null;
            while (await Receive(client, ct) is { } message)
            {
                switch (message["type"]!.GetValue<string>())
                {
                    case "call":
                        var (ok, result, error) = (broker ?? ((_, _) => (false, null, "no broker")))(message["op"]!.GetValue<string>(), message["args"]);
                        await Send(client, new JsonObject
                        {
                            ["type"] = "reply", ["id"] = message["id"]!.DeepClone(), ["ok"] = ok, ["result"] = result, ["error"] = error,
                        }, ct);
                        break;
                    case "log":
                        logs.Add($"{message["level"]}: {message["message"]}");
                        break;
                    case "done":
                        done = message;
                        break;
                }
            }
            await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
            await serving;
            return new SessionRun(done ?? throw new InvalidOperationException("No done message."), logs);
        }
    }

    private static async Task<(WebSocket Server, WebSocket Client)> PairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var tcp = new TcpClient();
            var connect = tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var accepted = await listener.AcceptTcpClientAsync();
            await connect;
            return (WebSocket.CreateFromStream(accepted.GetStream(), new WebSocketCreationOptions { IsServer = true }),
                    WebSocket.CreateFromStream(tcp.GetStream(), new WebSocketCreationOptions { IsServer = false }));
        }
        finally
        {
            listener.Stop();
        }
    }

    private static Task Send(WebSocket socket, JsonObject message, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonObject?> Receive(WebSocket socket, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await socket.ReceiveAsync(chunk, ct);
            if (read.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            buffer.Write(chunk, 0, read.Count);
            if (read.EndOfMessage)
            {
                return JsonNode.Parse(buffer.ToArray())!.AsObject();
            }
        }
    }
}
