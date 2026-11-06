using System.CommandLine;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Dcms.Cli;

/// <summary>
/// The commands (ADR 0031, 0032). What the server says is authoritative: <c>check</c> and
/// <c>run</c> send the working tree to the same build the web IDE uses, and <c>deploy</c> merges
/// main into release on the server, as the console does.
/// </summary>
internal sealed class Commands(Config config, Api api, HttpClient http, TextWriter stdout, TextWriter stderr, TextReader stdin, string cwd)
{
    public static string Version =>
        typeof(Commands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    public RootCommand Build()
    {
        var root = new RootCommand("dcms: your DCMS workspace from the desktop, for you and your AI agents.");

        var server = new Option<string?>("--server") { Description = "The console's address, e.g. https://admin.example.com" };
        var auth = new Option<string?>("--auth") { Description = "Identity's address, when it is not auth.<console's domain>" };
        Add(root, new Command("login", "Sign in through your browser.") { server, auth },
            async (r, ct) => { await api.LoginAsync(r.GetValue(server), r.GetValue(auth), ct); return 0; });

        Add(root, new Command("logout", "Forget this machine's sign-in."), (_, _) =>
        {
            config.RemoveCredentials();
            stdout.WriteLine("Signed out on this machine. To end the sign-in everywhere, remove it under Account → Devices.");
            return Task.FromResult(0);
        });

        var remote = new Option<string>("--remote") { Description = "The git remote the clone came from.", DefaultValueFactory = _ => "origin" };
        Add(root, new Command("link", "Link this clone to its app.") { remote }, async (r, ct) =>
        {
            var name = r.GetValue(remote)!;
            var dir = Clone.Root(cwd);
            var url = Clone.Git(dir, "remote", "get-url", name);
            var found = await api.CallAsync($"/api/admin/me/repos?remote={Uri.EscapeDataString(url)}", null, null, null, ct);
            var link = new Link(Api.Str(found?["tenant"])!, Api.Str(found?["environment"])!, Api.Str(found?["app"])!, Api.Str(found?["repoId"])!,
                Api.Str(found?["plugin"]), Api.Str(found?["repo"]), new Remote(name, url));
            Clone.WriteLink(config, dir, link);
            stdout.WriteLine($"Linked to {link.App} in {link.Tenant} ({link.Environment}). Next: dcms sdk");
            return 0;
        });

        Add(root, new Command("sdk", "Write .dcms/ (the SDK) and the editor config."), async (_, ct) =>
        {
            var (dir, link) = Linked();
            var sdk = await api.FunctionsAsync(link, "/sdk", ct: ct);
            var files = new Dictionary<string, byte[]>
            {
                [".dcms/functions.ts"] = Clone.Utf8(Api.Str(sdk?["typings"]) ?? ""),
                [".dcms/Sdk.g.cs"] = Clone.Utf8(Api.Str(sdk?["csharp"]) ?? ""),
            };
            foreach (var (k, v) in Clone.EditorFiles()) files[k] = v;
            // C#: the server's Dcms.Functions.Sdk in a package folder of the clone's own, and a project
            // that references it. Written once; yours to change after.
            if (Api.Str(sdk?["package"]) is { } package && Clone.HasCSharp(dir))
            {
                var nupkg = await api.BytesAsync($"/api/admin/plugins/{Uri.EscapeDataString(link.App)}/_functions/sdk.nupkg", link, ct);
                foreach (var (k, v) in Clone.CSharpFiles(package, nupkg, p => File.Exists(Path.Combine(dir, p)))) files[k] = v;
            }
            var changed = Clone.WriteFiles(dir, files);
            Clone.IgnoreDcms(dir);
            stdout.WriteLine(changed.Count > 0 ? $"Wrote {string.Join(", ", changed)}." : "Up to date.");
            return 0;
        });

        Add(root, new Command("check", "Build the working tree on the server."), async (_, ct) =>
        {
            var (dir, link) = Linked();
            var result = await api.FunctionsAsync(link, "/check", new JsonObject { ["files"] = Files(dir) }, ct: ct);
            foreach (var p in result?["problems"]?.AsArray() ?? []) stdout.WriteLine(Clone.FormatProblem(p));
            if (result?["ok"]?.GetValue<bool>() != true) return 1;
            var functions = result["functions"]?.AsArray().Select(f => (string?)f).ToList() ?? [];
            stdout.WriteLine($"OK: {(functions.Count > 0 ? string.Join(", ", functions) : "no functions")}.");
            if (result["permissions"]?.AsArray() is { Count: > 0 } needs) stdout.WriteLine($"Deploying needs: {string.Join(", ", needs.Select(n => (string?)n))}.");
            return 0;
        });

        var function = new Argument<string>("function") { Description = "The function's name, e.g. orders/total." };
        var input = new Option<string?>("--input") { Description = "Its input: JSON, or @file with JSON in it." };
        var live = new Option<bool>("--live") { Description = "Run what is deployed, not the working tree." };
        Add(root, new Command("run", "Run a function: the working tree, or what is live.") { function, input, live }, async (r, ct) =>
        {
            var (dir, link) = Linked();
            var body = new JsonObject { ["function"] = r.GetValue(function), ["input"] = Clone.ParseInput(r.GetValue(input)) };
            if (!r.GetValue(live)) body["files"] = Files(dir);
            var outcome = await api.FunctionsAsync(link, r.GetValue(live) ? "/run" : "/test", body, ct: ct);
            if (Api.Str(outcome?["logs"]) is { Length: > 0 } logs) stderr.Write(logs.EndsWith('\n') ? logs : logs + "\n");
            stdout.Write(Clone.Pretty(outcome?["result"]?.DeepClone() ?? JsonValue.Create((string?)null)!));
            stderr.WriteLine($"{Api.Str(outcome?["status"])} in {outcome?["durationMs"]} ms, {outcome?["calls"]} host call(s).");
            if (Api.Str(outcome?["error"]) is { } error)
            {
                stderr.WriteLine(error);
                return 1;
            }
            return 0;
        });

        var which = new Argument<string?>("function") { Description = "Only this function's runs.", Arity = ArgumentArity.ZeroOrOne };
        Add(root, new Command("runs", "The newest runs.") { which }, async (r, ct) =>
        {
            var (_, link) = Linked();
            var name = r.GetValue(which);
            var page = await api.FunctionsAsync(link, "/runs" + (name is null ? "" : $"?function={Uri.EscapeDataString(name)}"), ct: ct);
            foreach (var run in page?["items"]?.AsArray() ?? [])
            {
                var error = Api.Str(run?["error"]);
                stdout.WriteLine($"{run?["id"]}  {run?["createdAt"]}  {Api.Str(run?["function"]),-24} {Api.Str(run?["trigger"]),-8} {Api.Str(run?["status"]),-9} {run?["durationMs"]} ms{(error is null ? "" : "  " + error)}");
            }
            return 0;
        });

        var runId = new Argument<string>("runId") { Description = "The run's id (dcms runs lists them)." };
        Add(root, new Command("logs", "One run: input, result, logs.") { runId }, async (r, ct) =>
        {
            var (_, link) = Linked();
            var run = await api.FunctionsAsync(link, $"/runs/{Uri.EscapeDataString(r.GetValue(runId)!)}", ct: ct);
            stdout.Write(Clone.Pretty(new JsonObject
            {
                ["input"] = run?["input"]?.DeepClone(),
                ["result"] = run?["result"]?.DeepClone(),
                ["error"] = run?["run"]?["error"]?.DeepClone(),
            }));
            if (Api.Str(run?["logs"]) is { Length: > 0 } logs) stdout.Write(logs.EndsWith('\n') ? logs : logs + "\n");
            return 0;
        });

        root.Subcommands.Add(Secrets());

        Add(root, new Command("deploy", "Merge main into release, wait for the build, deploy it."), async (_, ct) =>
        {
            var (_, link) = Linked();
            stdout.WriteLine($"Deploying {link.App} in {link.Tenant} ({link.Environment}).");
            var merge = await api.CallAsync($"/api/admin/repos/{Uri.EscapeDataString(link.RepoId)}/git/merge", link, new JsonObject { ["head"] = "main" }, null, ct);
            if (merge?["conflict"]?.GetValue<bool>() == true)
            {
                foreach (var f in merge["files"]?.AsArray() ?? []) stderr.WriteLine($"conflict: {Api.Str(f?["path"])}");
                throw new CliException("main does not merge into release cleanly. Resolve it in the IDE, then deploy again.");
            }
            var sha = Api.Str(merge?["sha"]);
            var shortSha = sha is { Length: > 7 } ? sha[..7] : sha;
            stdout.WriteLine(merge?["upToDate"]?.GetValue<bool>() == true ? $"release is already at main ({shortSha})." : $"Merged main into release ({shortSha}). Building…");
            var until = DateTime.UtcNow.AddMinutes(10);
            while (DateTime.UtcNow < until)
            {
                var build = (await api.FunctionsAsync(link, "/deployments", ct: ct))?.AsArray().FirstOrDefault(d => Api.Str(d?["commitSha"]) == sha);
                var status = Api.Str(build?["status"]);
                if (status == "Active")
                {
                    stdout.WriteLine($"#{build!["number"]} is live.");
                    return 0;
                }
                if (status == "Failed") throw new CliException($"#{build!["number"]} did not build: {Api.Str(build["error"]) ?? "see the Deployments screen"}");
                if (build is not null && status != "Building")
                {
                    var activated = await api.FunctionsAsync(link, $"/deployments/{Api.Str(build["id"])}/activate", new JsonObject(), ct: ct);
                    stdout.WriteLine($"#{activated?["number"]} is live: {string.Join(", ", activated?["functions"]?.AsArray().Select(f => (string?)f) ?? [])}.");
                    return 0;
                }
                await Task.Delay(3000, ct);
            }
            throw new CliException("The build did not finish in 10 minutes. Check the Deployments screen.");
        });

        Add(root, new Command("version", "This command line's version, and the console's."), async (_, ct) =>
        {
            stdout.WriteLine($"dcms {Version}");
            if (config.ReadCredentials()?.Server is not { } at) return 0;
            var theirs = await ServerVersionAsync(at, ct);
            stdout.WriteLine($"{at}: {theirs ?? "unknown"}");
            if (theirs is not null && theirs != Version) stderr.WriteLine("The console serves another version. Run `dcms update`.");
            return 0;
        });

        var updateServer = new Option<string?>("--server") { Description = "Update from this console instead of the one signed in to." };
        Add(root, new Command("update", "Replace this command line with the console's build of it.") { updateServer },
            async (r, ct) => await UpdateAsync(r.GetValue(updateServer), ct));

        return root;
    }

    private Command Secrets()
    {
        var secrets = new Command("secrets", "The app's secrets: list names, set one, remove one.");
        Add(secrets, new Command("list", "The names of the secrets set."), async (_, ct) => await ListSecrets(ct));
        secrets.SetAction(async (_, ct) => await Guard(() => ListSecrets(ct)));

        var name = new Argument<string>("name");
        var value = new Argument<string?>("value") { Description = "The value; read from stdin if absent.", Arity = ArgumentArity.ZeroOrOne };
        Add(secrets, new Command("set", "Set a secret (stdin if no value).") { name, value }, async (r, ct) =>
        {
            var (_, link) = Linked();
            var secret = r.GetValue(value) ?? stdin.ReadToEnd().TrimEnd('\n').TrimEnd('\r');
            await api.FunctionsAsync(link, $"/secrets/{Uri.EscapeDataString(r.GetValue(name)!)}", new JsonObject { ["value"] = secret }, HttpMethod.Put, ct);
            stdout.WriteLine($"Set {r.GetValue(name)}.");
            return 0;
        });
        var removed = new Argument<string>("name");
        Add(secrets, new Command("rm", "Remove a secret.") { removed }, async (r, ct) =>
        {
            var (_, link) = Linked();
            await api.FunctionsAsync(link, $"/secrets/{Uri.EscapeDataString(r.GetValue(removed)!)}", null, HttpMethod.Delete, ct);
            stdout.WriteLine($"Removed {r.GetValue(removed)}.");
            return 0;
        });
        return secrets;
    }

    private async Task<int> ListSecrets(CancellationToken ct)
    {
        var (_, link) = Linked();
        foreach (var n in (await api.FunctionsAsync(link, "/secrets", ct: ct))?.AsArray() ?? []) stdout.WriteLine((string?)n);
        return 0;
    }

    private (string Dir, Link Link) Linked()
    {
        var dir = Clone.Root(cwd);
        return (dir, Clone.ReadLink(config, dir));
    }

    private static JsonObject Files(string dir)
    {
        var files = new JsonObject();
        foreach (var (k, v) in Clone.WorkingFiles(dir)) files[k] = v;
        return files;
    }

    /// <summary>What a published build is called for this machine: <c>dcms-linux-x64</c>, <c>dcms-win-x64.exe</c>, …</summary>
    public static string BuildName()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux"
            : throw new CliException("There is no build of dcms for this operating system.");
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var other => throw new CliException($"There is no build of dcms for {other}."),
        };
        return $"dcms-{os}-{arch}{(os == "win" ? ".exe" : "")}";
    }

    private async Task<string?> ServerVersionAsync(string server, CancellationToken ct)
    {
        try
        {
            using var res = await http.GetAsync($"{server}/downloads/version", ct);
            return res.IsSuccessStatusCode ? (await res.Content.ReadAsStringAsync(ct)).Trim() : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// The console's build for this machine, checked against the console's SHA256SUMS, put in place
    /// of the running file. Windows cannot overwrite a running .exe, but it can rename one.
    /// </summary>
    private async Task<int> UpdateAsync(string? serverOption, CancellationToken ct)
    {
        var server = serverOption is not null ? Api.Origin(serverOption)
            : config.ReadCredentials()?.Server ?? throw new CliException("From which console? dcms update --server https://admin.example.com");
        var name = BuildName();
        var sums = await http.GetStringAsync($"{server}/downloads/SHA256SUMS", ct);
        var expected = sums.Split('\n').Select(l => l.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*') == name)?[0]
            ?? throw new CliException($"{server} does not serve {name}.");
        var bytes = await http.GetByteArrayAsync($"{server}/downloads/{name}", ct);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException($"{name} from {server} is not what its SHA256SUMS says: not installed.");
        }
        var self = Environment.ProcessPath ?? throw new CliException("Cannot tell where this command line is installed.");
        var fresh = self + ".new";
        await File.WriteAllBytesAsync(fresh, bytes, ct);
        if (OperatingSystem.IsWindows())
        {
            var old = self + ".old";
            File.Delete(old);
            File.Move(self, old);
        }
        else
        {
            File.SetUnixFileMode(fresh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        File.Move(fresh, self, overwrite: true);
        stdout.WriteLine($"Updated to {server}'s build: {(await ServerVersionAsync(server, ct)) ?? "unknown version"}.");
        return 0;
    }

    private void Add(Command parent, Command command, Func<ParseResult, CancellationToken, Task<int>> action)
    {
        command.SetAction(async (r, ct) => await Guard(() => action(r, ct)));
        parent.Subcommands.Add(command);
    }

    /// <summary>A failure the person can act on is a line on stderr and an exit code, never a stack trace.</summary>
    private async Task<int> Guard(Func<Task<int>> action)
    {
        try
        {
            return await action();
        }
        catch (CliException e)
        {
            stderr.WriteLine(e.Message);
            return e.ExitCode;
        }
        catch (HttpRequestException e)
        {
            stderr.WriteLine($"Could not reach the server: {e.Message}");
            return 1;
        }
    }
}
