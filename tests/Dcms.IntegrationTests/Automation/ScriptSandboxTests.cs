extern alias ScriptRunnerApp;

using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScriptRunnerApp::Dcms.ScriptRunner;

namespace Dcms.IntegrationTests.Automation;

/// <summary>
/// The real script sandbox (ADR 0025): script-runner's DenoSandbox driving the Deno release the
/// image ships. Every escape the spike tried is tried again here, so a Deno upgrade that loosens
/// a permission check fails this suite instead of reaching a tenant.
/// </summary>
public sealed class ScriptSandboxTests(ScriptSandboxTests.Deno deno) : IClassFixture<ScriptSandboxTests.Deno>
{
    /// <summary>The Deno binary, copied out of the official image of the version the Dockerfile pins.</summary>
    public sealed class Deno : IAsyncLifetime
    {
        // Keep in step with src/Services/Dcms.ScriptRunner/Dockerfile (denoland/deno:bin-<same version>).
        private const string Image = "denoland/deno:distroless-2.5.4";

        public string Path { get; private set; } = "";

        public async ValueTask InitializeAsync()
        {
            // The binary is Linux's, as script-runner's image is.
            if (!DockerFactAttribute.Available || !OperatingSystem.IsLinux())
            {
                return;
            }
            await using var container = new ContainerBuilder(Image)
                .WithCommand("eval", "await new Promise(() => {})")
                .Build();
            await container.StartAsync();
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"deno-{Guid.NewGuid():N}");
            await File.WriteAllBytesAsync(Path, await container.ReadFileAsync("/bin/deno"));
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        public ValueTask DisposeAsync()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
            return ValueTask.CompletedTask;
        }
    }

    private DenoSandbox Sandbox(Action<ScriptRunnerOptions>? configure = null)
    {
        var options = new ScriptRunnerOptions { DenoPath = deno.Path };
        configure?.Invoke(options);
        return new DenoSandbox(Options.Create(options), NullLogger<DenoSandbox>.Instance);
    }

    private Task<RunResult> Run(string code, string ctx = "{}", int timeoutMs = 2_000, string language = "ts") =>
        Sandbox().RunAsync(new RunRequest(code, JsonNode.Parse(ctx), language, timeoutMs), TestContext.Current.CancellationToken);

    [DockerFact]
    public async Task A_typescript_function_gets_its_ctx_and_returns_json()
    {
        var result = await Run("""
            interface Ctx { row: { amount: number | null }; args: { limit: number } }
            export default async function main(ctx: Ctx): Promise<{ big: boolean; note: string }> {
              await new Promise((r) => setTimeout(r, 5));
              return { big: (ctx.row.amount ?? 0) > ctx.args.limit, note: "{{ stays as written }}" };
            }
            """, """{ "row": { "amount": 500 }, "args": { "limit": 100 } }""");

        result.Status.Should().Be("ok");
        result.Result!.ToJsonString().Should().Be("""{"big":true,"note":"{{ stays as written }}"}""");
    }

    [DockerFact]
    public async Task Javascript_runs_as_javascript()
    {
        var result = await Run("export default (ctx) => ctx.n * 2", """{ "n": 21 }""", language: "js");
        result.Result!.GetValue<int>().Should().Be(42);
    }

    [DockerTheory]
    [InlineData("import x from 'file:///etc/passwd'; export default () => x", "read access")]
    [InlineData("import x from '/etc/hostname' with { type: 'json' }; export default () => x", "")]
    [InlineData("export default async () => await import('file:///etc/hostname', { with: { type: 'json' } })", "read access")]
    [InlineData("export default async () => { const p = ['file:///etc', 'hostname'].join('/'); return await import(p) }", "read access")]
    [InlineData("export default () => Deno.readTextFileSync('/proc/self/environ')", "read access")]
    [InlineData("export default async () => (await fetch('file:///etc/passwd')).text()", "read access")]
    [InlineData("export default async () => (await fetch('http://169.254.169.254/')).status", "net access")]
    [InlineData("export default () => Deno.env.toObject()", "env access")]
    [InlineData("export default async () => await new Deno.Command('/bin/sh').output()", "run access")]
    [InlineData("export default () => Deno.dlopen('libc.so.6', {})", "ffi access")]
    [InlineData("export default () => Deno.writeTextFileSync('/tmp/x', 'x')", "write access")]
    [InlineData("import x from 'npm:left-pad'; export default () => x", "")]
    [InlineData("import x from 'https://deno.land/std/version.ts'; export default () => x", "")]
    public async Task Every_way_out_of_the_sandbox_is_refused(string code, string reason)
    {
        var result = await Run(code);

        result.Status.Should().Be("error", code);
        if (reason != "")
        {
            result.Error.Should().Contain(reason);
        }
        result.Error.Should().NotContain("root:", "nothing of a file it may not read may surface in the error");
    }

    [DockerFact]
    public async Task Limits_end_the_script_and_say_which()
    {
        (await Run("export default () => { while (true) {} }", timeoutMs: 500)).Should().BeEquivalentTo(new { Status = "limit" });
        (await Run("export default () => { const a = []; while (true) a.push('x'.repeat(1e6) + Math.random()) }"))
            .Error.Should().Contain("memory");
        (await Run("export default () => 'x'.repeat(200_000)")).Error.Should().Contain("larger than");
        // Off the V8 heap: an ArrayBuffer is not covered by --max-old-space-size, only by RLIMIT_DATA.
        (await Run("export default () => { const b = []; for (let i = 0; i < 12; i++) { const a = new Uint8Array(100 * 1024 * 1024); a.fill(1); b.push(a) } return b.length }"))
            .Status.Should().NotBe("ok", "a script cannot take memory the heap cap does not count");
        (await Run("export default () => { while (true) Deno.stdout.writeSync(new Uint8Array(65536)) }")).Status.Should().Be("limit");
    }

    [DockerFact]
    public async Task A_broken_script_says_what_is_wrong_without_its_source()
    {
        var syntax = await Run("export default () => {");
        syntax.Status.Should().Be("error");
        syntax.Error.Should().NotContain("base64");

        (await Run("export const x = 1")).Error.Should().Contain("default function");
        (await Run("export default () => { throw new Error('nope') }")).Error.Should().Be("nope");
    }

    [DockerFact]
    public async Task Runs_at_once_share_nothing()
    {
        // One run stashes a value wherever it could; the next must find none of it.
        var first = Run("export default () => { (globalThis as any).stash = 'secret'; return 1 }");
        var second = Run("export default () => (globalThis as any).stash ?? null");
        await Task.WhenAll(first, second);
        (await Run("export default () => (globalThis as any).stash ?? null")).Result.Should().BeNull();
        Directory.GetDirectories(System.IO.Path.GetTempPath(), "deno-*").Should().NotContain(d => d.Contains(deno.Path),
            "each run's cache directory is deleted with it");
    }
}
