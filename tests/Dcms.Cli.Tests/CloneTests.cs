using System.Diagnostics;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Dcms.Cli;
using Xunit;

namespace Dcms.Cli.Tests;

/// <summary>The files the CLI keeps and writes, and a clone's limits: no server needed.</summary>
public sealed class CloneTests : IDisposable
{
    private readonly List<string> _temps = [];

    private string Temp()
    {
        var dir = Directory.CreateTempSubdirectory("dcms-cli-").FullName;
        _temps.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _temps) Directory.Delete(d, recursive: true);
    }

    private static void Run(string dir, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        p.WaitForExit();
        p.ExitCode.Should().Be(0, p.StandardError.ReadToEnd());
    }

    private static Dictionary<string, byte[]> Text(params (string Path, string Text)[] files) =>
        files.ToDictionary(f => f.Path, f => Clone.Utf8(f.Text));

    [Fact]
    public void Sign_in_is_on_the_auth_host_beside_the_console()
    {
        Api.AuthOrigin("https://admin.highgeek.eu/x").Should().Be("https://auth.highgeek.eu");
        Api.AuthOrigin("http://localhost:8080").Should().Be("http://localhost:8080");
    }

    [Fact]
    public void Credentials_are_private_to_the_user_and_a_reply_without_a_refresh_token_keeps_the_old_one()
    {
        var config = new Config(Path.Combine(Temp(), "dcms"));
        config.WriteCredentials(new Credentials("https://admin.x", "https://auth.x/token", "a", "r", 0));
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(config.CredentialsPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(config.Dir).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var next = Api.Kept(config.ReadCredentials(), "https://admin.x", "https://auth.x/token", JsonNode.Parse("""{"access_token":"b","expires_in":60}""")!, 0);
        next.Should().Be(new Credentials("https://admin.x", "https://auth.x/token", "b", "r", 60_000));
    }

    [Fact]
    public void The_node_command_lines_sign_in_is_read_as_it_was()
    {
        var config = new Config(Temp());
        File.WriteAllText(config.CredentialsPath, """{"server":"https://admin.x","tokenEndpoint":"https://auth.x/t","accessToken":"a","refreshToken":"r","expiresAt":123}""");
        config.ReadCredentials().Should().Be(new Credentials("https://admin.x", "https://auth.x/t", "a", "r", 123));
    }

    [Fact]
    public void The_working_tree_is_what_git_does_not_ignore_less_dcms_and_binaries()
    {
        var root = Temp();
        Run(root, "init", "-q");
        Clone.WriteFiles(root, Text(("functions/a.ts", "export {}\n"), (".dcms/functions.ts", "x"), ("dist/out.js", "y"), (".gitignore", "dist/\n")));
        File.WriteAllBytes(Path.Combine(root, "logo.png"), [137, 80, 0, 1]);
        Clone.WorkingFiles(root).Keys.Should().Equal("functions/a.ts");
    }

    [Fact]
    public void Dcms_is_ignored_once()
    {
        var root = Temp();
        File.WriteAllText(Path.Combine(root, ".gitignore"), "node_modules");
        Clone.IgnoreDcms(root).Should().BeTrue();
        Clone.IgnoreDcms(root).Should().BeFalse();
        File.ReadAllText(Path.Combine(root, ".gitignore")).Should().Be("node_modules\n.dcms/\nbin/\nobj/\n");
    }

    [Fact]
    public void Editor_config_sees_the_sdk_and_the_runtime_the_server_builds_against()
    {
        var files = Clone.EditorFiles();
        var deno = JsonNode.Parse(files["deno.json"])!;
        ((string?)deno["imports"]!["@dcms/functions"]).Should().Be("./.dcms/functions.ts");
        deno["compilerOptions"]!["lib"]!.AsArray().Select(l => (string?)l).Should().Equal("esnext", "webworker");
        ((string?)deno["nodeModulesDir"]).Should().Be("manual");
        ((string?)JsonNode.Parse(files["tsconfig.json"])!["compilerOptions"]!["paths"]!["@dcms/functions"]![0]).Should().Be("./.dcms/functions.ts");
    }

    [Fact]
    public void Input_is_json_inline_or_from_a_file()
    {
        Clone.ParseInput(null).Should().BeNull();
        ((int)Clone.ParseInput("""{"a":1}""")!["a"]!).Should().Be(1);
        ((int)Clone.ParseInput("@in.json", _ => "[2]")![0]!).Should().Be(2);
        var bad = () => Clone.ParseInput("nope");
        bad.Should().Throw<CliException>().WithMessage("*must be JSON*");
    }

    [Fact]
    public void Problems_print_as_file_line_column()
    {
        Clone.FormatProblem(JsonNode.Parse("""{"file":"functions/a.ts","line":3,"column":7,"message":"Bad.","code":"TS2322"}"""))
            .Should().Be("functions/a.ts:3:7: Bad. (TS2322)");
        Clone.FormatProblem(JsonNode.Parse("""{"message":"Nothing to build."}""")).Should().Be("Nothing to build.");
        Clone.FormatProblem(JsonNode.Parse("""{"file":"a.cs","line":null,"message":"m"}""")).Should().Be("a.cs: m");
    }

    [Fact]
    public void A_csharp_repository_gets_the_servers_sdk_package_and_a_project_and_source_only_when_it_has_none()
    {
        var root = Temp();
        Run(root, "init", "-q");
        Clone.WriteFiles(root, Text(("functions/a.ts", "export {}\n")));
        Clone.HasCSharp(root).Should().BeFalse();
        Clone.WriteFiles(root, Text(("functions/Ping.cs", "class Ping {}\n")));
        Clone.HasCSharp(root).Should().BeTrue();

        var fresh = Clone.CSharpFiles("1.0.0-sdk.abc", [1], _ => false);
        fresh.Keys.Order().Should().Equal(".dcms/packages/dcms.functions.sdk.1.0.0-sdk.abc.nupkg", ".dcms/version.props", "Functions.csproj", "nuget.config");
        System.Text.Encoding.UTF8.GetString(fresh[".dcms/version.props"]).Should().Contain("<DcmsFunctionsSdkVersion>1.0.0-sdk.abc<");
        Clone.CSharpFiles("1.0.0-sdk.abc", [1], _ => true).Should().HaveCount(2);
        var odd = () => Clone.CSharpFiles("1.0;rm", [1], _ => true);
        odd.Should().Throw<CliException>();
        Clone.WriteFiles(root, new Dictionary<string, byte[]> { ["bytes.bin"] = [0, 1] }).Should().Equal("bytes.bin");
        Clone.WriteFiles(root, new Dictionary<string, byte[]> { ["bytes.bin"] = [0, 1] }).Should().BeEmpty();
    }

    [Fact]
    public void A_clone_cannot_point_a_read_or_a_write_outside_itself_or_a_command_at_another_app()
    {
        if (OperatingSystem.IsWindows()) return; // symbolic links need privileges there
        var root = Temp();
        var outside = Temp();
        File.WriteAllText(Path.Combine(outside, "id_rsa"), "secret");
        Run(root, "init", "-q");
        Clone.WriteFiles(root, Text(("functions/a.ts", "export {}\n"), (".env", "KEY=1\n"), ("README.md", "# x\n")));
        File.CreateSymbolicLink(Path.Combine(root, "functions", "key.ts"), Path.Combine(outside, "id_rsa"));
        Directory.CreateSymbolicLink(Path.Combine(root, "lib"), outside);
        Clone.WorkingFiles(root).Keys.Should().Equal("functions/a.ts");

        File.CreateSymbolicLink(Path.Combine(root, "deno.json"), Path.Combine(outside, "id_rsa"));
        var throughFile = () => Clone.WriteFiles(root, Text(("deno.json", "{}")));
        throughFile.Should().Throw<CliException>().WithMessage("*symbolic link*");
        var throughFolder = () => Clone.WriteFiles(root, Text(("lib/x.ts", "")));
        throughFolder.Should().Throw<CliException>().WithMessage("*symbolic link*");
        var escape = () => Clone.WriteFiles(root, Text(("../x.ts", "")));
        escape.Should().Throw<CliException>();
        File.ReadAllText(Path.Combine(outside, "id_rsa")).Should().Be("secret");

        // The link lives with the sign-in, keyed by the clone's path; the clone cannot supply one.
        var config = new Config(Temp());
        Directory.CreateDirectory(Path.Combine(root, ".dcms"));
        File.WriteAllText(Path.Combine(root, ".dcms", "link.json"), """{"app":"other"}""");
        var unlinked = () => Clone.ReadLink(config, root);
        unlinked.Should().Throw<CliException>().WithMessage("*not linked*");
        Run(root, "remote", "add", "origin", "https://git.example.test/tenant-acme/functions-1.git");
        var origin = new Remote("origin", "https://git.example.test/tenant-acme/functions-1.git");
        var link = new Link("acme", "production", "fn", "0b0e7c2e-1111-4222-8333-944445555666", "functions", "tenant-acme/functions-1", origin);
        Clone.WriteLink(config, root, link);
        Clone.ReadLink(config, root).App.Should().Be("fn");
        var odd = () => Clone.WriteLink(config, root, link with { RepoId = "../x" });
        odd.Should().Throw<CliException>().WithMessage("*does not understand*");
        // Another repository cloned into the same folder later is not linked by it.
        Run(root, "remote", "set-url", "origin", "https://git.example.test/tenant-evil/functions-2.git");
        var swapped = () => Clone.ReadLink(config, root);
        swapped.Should().Throw<CliException>().WithMessage("*different clone*");

        File.CreateSymbolicLink(Path.Combine(root, ".gitignore"), Path.Combine(outside, "id_rsa"));
        var ignore = () => Clone.IgnoreDcms(root);
        ignore.Should().Throw<CliException>().WithMessage("*symbolic link*");
        File.ReadAllText(Path.Combine(outside, "id_rsa")).Should().Be("secret");
    }

    [Fact]
    public void Each_platform_has_its_own_build_name()
    {
        Commands.BuildName().Should().MatchRegex(@"^dcms-(linux|osx|win)-(x64|arm64)(\.exe)?$");
    }
}
