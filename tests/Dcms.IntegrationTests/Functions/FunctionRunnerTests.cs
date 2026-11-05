extern alias ScriptRunnerApp;

using System.Text.Json.Nodes;
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
public sealed class FunctionRunnerTests(DenoBinary deno, FunctionRunnerTests.Esbuild esbuild)
    : IClassFixture<DenoBinary>, IClassFixture<FunctionRunnerTests.Esbuild>
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

    /// <summary>
    /// The TypeScript compiler of the version script-runner's Dockerfile pins, which reads
    /// functions' types into JSON Schema: the repo's pnpm copy when there is one, else that same
    /// npm tarball, checked against the same checksum.
    /// </summary>
    public sealed class TypeScript : IAsyncLifetime
    {
        // Keep in step with src/Services/Dcms.ScriptRunner/Dockerfile (TYPESCRIPT_VERSION, TYPESCRIPT_SHA256).
        private const string Version = "5.9.3";
        private const string Sha256 = "10e108c9cf7d5f2879053dff18515fb405abf2ccef63eaaf017d9c571687a1d3";

        public string Path { get; private set; } = "";
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
            var local = System.IO.Path.Combine(directory?.FullName ?? ".", "node_modules", ".pnpm", $"typescript@{Version}", "node_modules", "typescript");
            if (File.Exists(System.IO.Path.Combine(local, "lib", "typescript.js")))
            {
                Path = local;
                return;
            }

            using var http = new HttpClient();
            var tarball = await http.GetByteArrayAsync($"https://registry.npmjs.org/typescript/-/typescript-{Version}.tgz");
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(tarball)).Should().Be(Sha256);
            _downloaded = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"typescript-{Guid.NewGuid():N}");
            Directory.CreateDirectory(System.IO.Path.Combine(_downloaded, "lib"));
            await using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(tarball), System.IO.Compression.CompressionMode.Decompress);
            await using var tar = new System.Formats.Tar.TarReader(gzip);
            while (await tar.GetNextEntryAsync() is { } entry)
            {
                var name = entry.Name["package/".Length..];
                if (name == "lib/typescript.js" || (name.StartsWith("lib/lib.", StringComparison.Ordinal) && name.EndsWith(".d.ts", StringComparison.Ordinal)))
                {
                    await entry.ExtractToFileAsync(System.IO.Path.Combine(_downloaded, name), overwrite: false);
                }
            }
            Path = _downloaded;
        }

        public ValueTask DisposeAsync()
        {
            if (_downloaded is not null && Directory.Exists(_downloaded))
            {
                Directory.Delete(_downloaded, recursive: true);
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

    private Task<BuildResult> Build(Dictionary<string, string> files, Action<ScriptRunnerOptions>? configure = null,
        List<PackageArchive>? packages = null) =>
        new FunctionBuilder(Microsoft.Extensions.Options.Options.Create(Options(configure)), NullLogger<FunctionBuilder>.Instance)
            .BuildAsync(new BuildRequest(files, SdkTypes, Packages: packages), TestContext.Current.CancellationToken);

    /// <summary>An npm tarball, as `npm pack` makes one: every file under "package/".</summary>
    public static string Tarball(Dictionary<string, string> files)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new System.Formats.Tar.TarWriter(gzip, System.Formats.Tar.TarEntryFormat.Ustar))
        {
            foreach (var (path, text) in files)
            {
                var entry = new System.Formats.Tar.UstarTarEntry(System.Formats.Tar.TarEntryType.RegularFile, path.StartsWith("package/") || path.StartsWith('/') || path.Contains("..") ? path : "package/" + path)
                {
                    DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)),
                };
                tar.WriteEntry(entry);
            }
        }
        return Convert.ToBase64String(buffer.ToArray());
    }

    public static readonly Dictionary<string, string> TinyLib = new()
    {
        ["package.json"] = """{ "name": "tiny-lib", "version": "1.2.0", "type": "module", "exports": { ".": { "types": "./index.d.ts", "default": "./index.js" }, "./extra": { "types": "./extra.d.ts", "default": "./extra.js" } } }""",
        ["index.js"] = "export const double = (n) => n * 2;",
        ["index.d.ts"] = "export declare const double: (n: number) => number;",
        ["extra.js"] = "export const greet = (name) => `hello ${name}`;",
        ["extra.d.ts"] = "export declare const greet: (name: string) => string;",
    };

    public static readonly Dictionary<string, string> NodeOnly = new()
    {
        ["package.json"] = """{ "name": "node-only", "version": "1.0.0", "type": "module", "main": "./index.js", "types": "./index.d.ts" }""",
        ["index.js"] = "import { readFileSync } from \"node:fs\"; import net from \"net\"; export const read = (p) => readFileSync(p, \"utf8\") + typeof net;",
        ["index.d.ts"] = "export declare const read: (path: string) => string;",
    };

    [DockerFact]
    public async Task A_function_imports_the_packages_it_depends_on_and_nothing_that_needs_nodejs()
    {
        var packages = new List<PackageArchive>
        {
            new("node_modules/tiny-lib", Tarball(TinyLib)),
            new("node_modules/node-only", Tarball(NodeOnly)),
        };
        var manifest = """{ "dependencies": { "tiny-lib": "^1.2.0", "node-only": "^1.0.0" } }""";
        var uses = """
            import { defineFunction } from "@dcms/functions";
            import { double } from "tiny-lib";
            import { greet } from "tiny-lib/extra";
            export default defineFunction({ handler: (input: { n: number }) => greet(String(double(input.n))) });
            """;

        var built = await Build(new() { ["package.json"] = manifest, ["functions/uses.ts"] = uses }, packages: packages);
        built.Diagnostics.Should().BeEmpty(string.Join("; ", built.Diagnostics.Select(d => $"{d.File}: {d.Message}")));
        var run = await Session(built.Functions.Single().Bundle, """{"n":21}""");
        run.Done["result"]!.GetValue<string>().Should().Be("hello 42");

        var nodeOnly = await Build(new()
        {
            ["package.json"] = manifest,
            ["functions/reads.ts"] = """
                import { defineFunction } from "@dcms/functions";
                import { read } from "node-only";
                export default defineFunction({ handler: () => read("/etc/passwd") });
                """,
        }, packages: packages);
        nodeOnly.Ok.Should().BeFalse();
        nodeOnly.Diagnostics.Should().ContainSingle().Which.Message.Should().Contain("\"node-only\" needs Node.js (fs, net)");

        var undeclared = await Build(new()
        {
            ["package.json"] = manifest,
            ["functions/pads.ts"] = """
                import { defineFunction } from "@dcms/functions";
                import pad from "left-pad";
                import { readFileSync } from "node:fs";
                export default defineFunction({ handler: () => pad + readFileSync });
                """,
        }, packages: packages);
        undeclared.Diagnostics.Select(d => d.Message).Should().Contain(m => m.Contains("add \"left-pad\" to package.json"))
            .And.Contain(m => m.Contains("'node:fs' is part of Node.js"));
    }

    [DockerFact]
    public async Task A_package_archive_cannot_escape_its_folder()
    {
        var evil = Tarball(new() { ["package/../../../functions/x.ts"] = "export default 1;" });
        var built = await Build(new()
        {
            ["package.json"] = """{ "dependencies": { "evil": "1.0.0" } }""",
            ["functions/f.ts"] = "import { defineFunction } from \"@dcms/functions\"; export default defineFunction({ handler: () => 1 });",
        }, packages: [new("node_modules/evil", evil)]);

        built.Ok.Should().BeFalse();
        built.Diagnostics.Single().Message.Should().Contain("would land outside the package");
    }


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

        built.Diagnostics.Should().BeEmpty(string.Join("; ", built.Diagnostics.Select(d => $"{d.File}: {d.Message}")));
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

    [DockerFact]
    public async Task A_function_is_checked_against_what_it_has_at_run_time()
    {
        var built = await Build(new()
        {
            ["functions/deno.ts"] = """
                import { defineFunction } from "@dcms/functions";
                export default defineFunction({ handler: () => Deno.readTextFileSync("/etc/hostname") });
                """,
            ["functions/dom.ts"] = """
                import { defineFunction } from "@dcms/functions";
                export default defineFunction({ handler: () => document.title });
                """,
            ["functions/web.ts"] = """
                import { defineFunction } from "@dcms/functions";
                export default defineFunction({
                  async handler() {
                    const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode("x"));
                    const response: Response = await fetch(new URL("https://example.com"), { method: "GET" });
                    return [digest.byteLength, response.status];
                  },
                });
                """,
        });

        built.Diagnostics.Select(d => (d.File, d.Code)).Should().BeEquivalentTo([("functions/deno.ts", "TS2304"), ("functions/dom.ts", "TS2584")]);
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

    // TypeScript reaches files without an import statement, and the type checker quotes what it
    // cannot parse: a reference to /etc/hostname once put the box's hostname in a diagnostic.
    [DockerTheory]
    [InlineData("/// <reference path=\"/etc/hostname\" />")]
    [InlineData("/// <reference types=\"/etc/hostname\" />")]
    [InlineData("// @ts-types=\"/etc/hostname\"\nimport \"./lib.ts\";")]
    [InlineData("// @deno-types=\"/etc/hostname\"\nimport \"./lib.ts\";")]
    [InlineData("const m = await import(`/etc/hostname`);")]
    [InlineData("const m = require('/etc/hostname');")]
    public async Task A_function_reaches_no_file_outside_the_repository_by_any_other_way(string line)
    {
        var hostname = (await File.ReadAllTextAsync("/etc/hostname")).Trim();
        var built = await Build(new()
        {
            ["functions/lib.ts"] = "export {}",
            ["functions/sneaky.ts"] = line + "\nimport { defineFunction } from \"@dcms/functions\";\nexport default defineFunction({ handler: () => 1 });\n",
        });

        built.Ok.Should().BeFalse(line);
        built.Functions.Should().BeEmpty();
        built.Diagnostics.Should().AllSatisfy(d => d.Message.Should().NotContain(hostname));
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
    // Deno and Node's process are gone before the function runs (and its types never had them).
    [InlineData("return (globalThis as any).Deno.readTextFileSync('/etc/hostname')", "undefined")]
    [InlineData("return (globalThis as any).Deno.env.toObject()", "undefined")]
    [InlineData("return await new (globalThis as any).Deno.Command('/bin/sh').output()", "undefined")]
    [InlineData("return (globalThis as any).process.env", "undefined")]
    // fetch is the platform's: a host call the broker decides (here, there is none).
    [InlineData("return (await fetch('http://169.254.169.254/')).status", "no broker")]
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

    private Task<SessionRun> Session(string bundle, string input = "null", string context = "{}",
        Func<string, JsonNode?, (bool Ok, JsonNode? Result, string? Error)>? broker = null,
        string limits = "{}", Action<ScriptRunnerOptions>? configure = null) =>
        SessionDriver.RunAsync(Options(configure), new JsonObject
        {
            ["type"] = "start",
            ["mode"] = "run",
            ["bundle"] = bundle,
            ["input"] = JsonNode.Parse(input),
            ["context"] = JsonNode.Parse(context),
            ["limits"] = JsonNode.Parse(limits),
        }, broker);
}
