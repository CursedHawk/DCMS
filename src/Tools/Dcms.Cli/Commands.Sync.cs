using System.CommandLine;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dcms.Cli;

/// <summary>
/// Sites and apps on this machine (ADR 0032, C5): <c>pull</c> a branch's files into a folder,
/// change them with anything, <c>status</c>/<c>diff</c>, <c>push</c> into your draft on the
/// server, <c>commit</c>, <c>publish</c>, watch the <c>builds</c>. Over the same routes as the web
/// IDE, with your sign-in (or an agent's token): no git credentials. Each one writes what it did to
/// a writer, so <c>dcms mcp</c> offers them to agents as tools too.
/// </summary>
internal sealed partial class Commands
{
    private static readonly Option<string> DirOption = new("--dir", "-d") { Description = "The folder; default: this one.", DefaultValueFactory = _ => "." };

    private string FullDir(string dir) => Path.GetFullPath(Path.Combine(cwd, dir));

    private static SyncState ReadState(string root)
    {
        var path = Path.Combine(root, Sync.StateFile);
        if (!File.Exists(path)) throw new CliException($"{root} is not a pulled site or app. `dcms pull <site|app> --dir {root}` makes it one.");
        return JsonSerializer.Deserialize(File.ReadAllBytes(path), CliJson.Default.SyncState)!;
    }

    private static void WriteState(string root, SyncState state)
    {
        if (!Clone.PlainUnder(root, Sync.StateFile)) throw new CliException($"Refusing to write {Sync.StateFile}: it is a symbolic link.");
        Directory.CreateDirectory(Path.Combine(root, ".dcms"));
        File.WriteAllBytes(Path.Combine(root, Sync.StateFile), JsonSerializer.SerializeToUtf8Bytes(state, CliJson.Default.SyncState));
    }

    private Task<HttpResponseMessage> In(SyncState s, HttpMethod verb, string path, JsonNode? body, CancellationToken ct) =>
        api.RawAsync(verb, $"/api/admin/{s.Workspace}{path}", s.Tenant, s.Environment,
            body is null ? null : new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), null, ct);

    /// <summary>A site by id or name, else a Functions app by its slug.</summary>
    private async Task<(string Workspace, string Name)> ResolveAsync(string target, string tenant, string? environment, CancellationToken ct)
    {
        var sites = (await api.CallInAsync("/api/admin/sites", tenant, environment, null, null, ct))?.AsArray() ?? [];
        if (sites.FirstOrDefault(s => string.Equals(Api.Str(s?["id"]), target, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(Api.Str(s?["name"]), target, StringComparison.OrdinalIgnoreCase)) is { } site)
        {
            return ($"sites/{Api.Str(site["id"])}", Api.Str(site["name"])!);
        }
        try
        {
            var repo = await api.CallInAsync($"/api/admin/plugins/{Uri.EscapeDataString(target)}/_functions/repo", tenant, environment, null, null, ct);
            if (Api.Str(repo?["id"]) is { } id) return ($"repos/{id}", target);
        }
        catch (CliException)
        {
            // no such app
        }
        throw new CliException($"No site or app called {target} here. `dcms op call sites.list` and `dcms op call plugins.list` list them.");
    }

    /// <summary>Your draft of the branch, as the server has it now (opening it creates it).</summary>
    private async Task<(string? BaseSha, Dictionary<string, string> Files)> DraftAsync(SyncState s, CancellationToken ct)
    {
        using var res = await In(s, HttpMethod.Get, $"/ide?branch={Uri.EscapeDataString(s.Branch)}", null, ct);
        var draft = await Api.JsonAsync(res, ct) ?? throw new CliException("The server sent no files.");
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, content) in draft["files"]?.AsObject() ?? []) files[path] = Api.Str(content) ?? "";
        return (Api.Str(draft["baseSha"]), files);
    }

    internal async Task<int> PullAsync(string? target, string dir, string? branch, string? tenant, string? environment, TextWriter w, CancellationToken ct)
    {
        var root = FullDir(dir);
        var existing = File.Exists(Path.Combine(root, Sync.StateFile)) ? ReadState(root) : null;
        SyncState state;
        if (existing is not null && target is null)
        {
            state = existing with { Branch = branch ?? existing.Branch };
        }
        else
        {
            if (target is null) throw new CliException("Which site or app? dcms pull <site|app> --dir <folder>");
            var (t, env) = tenant is not null ? (tenant, environment) : (existing?.Tenant ?? throw new CliException("Which workspace? Pass --workspace <slug>."), existing.Environment);
            var (workspace, name) = await ResolveAsync(target, t, env, ct);
            if (existing is not null && existing.Workspace != workspace)
            {
                throw new CliException($"{root} holds {existing.Name}, not {name}. Pull into another folder.");
            }
            if (existing is null && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            {
                throw new CliException($"{root} is not empty. Pull into a new folder, or one pulled before.");
            }
            state = existing ?? new SyncState(config.ReadCredentials()?.Server ?? api.Agent?.Server ?? "", t, env, workspace, name, branch ?? "main", null, []);
        }

        Directory.CreateDirectory(root);
        var (baseSha, server) = await DraftAsync(state, ct);
        var local = existing is null ? [] : Sync.LocalFiles(root);
        var (write, conflicts) = existing is null
            ? (server.ToDictionary(f => f.Key, f => (string?)f.Value), [])
            : Sync.Merge(local, existing.Hashes, server);
        var changed = Clone.WriteFiles(root, write.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => Sync.ToDisk(f.Key, f.Value!)));
        foreach (var gone in write.Where(f => f.Value is null).Select(f => f.Key))
        {
            if (Clone.PlainUnder(root, gone)) File.Delete(Path.Combine(root, gone));
        }
        WriteState(root, state with { BaseSha = baseSha, Hashes = server.ToDictionary(f => f.Key, f => Sync.Hash(f.Value)), Conflicts = null });
        w.WriteLine($"{state.Name} ({state.Branch}) in {root}: {server.Count} files, {changed.Count + write.Count(f => f.Value is null)} updated.");
        foreach (var c in conflicts) w.WriteLine($"conflict: {c} (both changed: {(Sync.IsBinary(c) ? "yours kept" : "markers written")})");
        if (conflicts.Count > 0) w.WriteLine("Resolve the markers, then `dcms push`.");
        return conflicts.Count > 0 ? 2 : 0;
    }

    internal int Status(string dir, TextWriter w)
    {
        var root = FullDir(dir);
        var state = ReadState(root);
        var (added, modified, deleted) = Sync.Changes(Sync.LocalFiles(root), state.Hashes);
        w.WriteLine($"{state.Name} ({state.Branch}) in {state.Tenant}{(state.Environment is null ? "" : $"/{state.Environment}")}");
        foreach (var p in added) w.WriteLine($"  added     {p}");
        foreach (var p in modified) w.WriteLine($"  modified  {p}");
        foreach (var p in deleted) w.WriteLine($"  deleted   {p}");
        if (added.Count + modified.Count + deleted.Count == 0) w.WriteLine("  nothing to push");
        if (state.Conflicts is { Length: > 0 } open) w.WriteLine($"  to resolve before `dcms commit --resolved`: {string.Join(", ", open)}");
        return 0;
    }

    internal async Task<int> DiffAsync(string dir, string? only, TextWriter w, CancellationToken ct)
    {
        var root = FullDir(dir);
        var state = ReadState(root);
        var local = Sync.LocalFiles(root);
        var (added, modified, deleted) = Sync.Changes(local, state.Hashes);
        var (_, server) = await DraftAsync(state, ct);
        var temp = Directory.CreateTempSubdirectory("dcms-diff-").FullName;
        try
        {
            foreach (var path in added.Concat(modified).Concat(deleted).Where(p => only is null || p == only).Where(p => !Sync.IsBinary(p)))
            {
                var theirs = Path.Combine(temp, "server", path);
                var mine = Path.Combine(temp, "yours", path);
                Directory.CreateDirectory(Path.GetDirectoryName(theirs)!);
                Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
                File.WriteAllText(theirs, server.GetValueOrDefault(path) ?? "");
                File.WriteAllText(mine, local.GetValueOrDefault(path) ?? "");
                var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, WorkingDirectory = temp };
                foreach (var a in new[] { "diff", "--no-index", "--no-color", $"server/{path}", $"yours/{path}" }) start.ArgumentList.Add(a);
                using var git = Process.Start(start) ?? throw new CliException("git is needed for dcms diff.");
                w.Write(await git.StandardOutput.ReadToEndAsync(ct));
                await git.WaitForExitAsync(ct);
            }
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
        return 0;
    }

    /// <summary>Up to this many characters a request: well under the server's body limit.</summary>
    private const int PushBatchChars = 8_000_000;

    internal async Task<int> PushAsync(string dir, TextWriter w, CancellationToken ct)
    {
        var root = FullDir(dir);
        var state = ReadState(root);
        var local = Sync.LocalFiles(root);
        var (added, modified, deleted) = Sync.Changes(local, state.Hashes);
        if (added.Count + modified.Count + deleted.Count == 0)
        {
            w.WriteLine("Nothing to push.");
            return 0;
        }
        var hashes = new Dictionary<string, string>(state.Hashes, StringComparer.Ordinal);
        var puts = added.Concat(modified).ToList();
        var batch = new JsonObject();
        var size = 0;
        async Task<bool> SendAsync(JsonObject put, JsonArray delete)
        {
            using var res = await In(state, HttpMethod.Patch, $"/ide/files?branch={Uri.EscapeDataString(state.Branch)}",
                new JsonObject { ["put"] = put, ["delete"] = delete, ["clientId"] = $"dcms:{Environment.MachineName}" }, ct);
            if (res.StatusCode == HttpStatusCode.Conflict)
            {
                var body = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
                foreach (var c in body?["conflicts"]?.AsArray() ?? []) w.WriteLine($"changed on the server meanwhile: {Api.Str(c?["path"])}");
                w.WriteLine("Nothing in this batch was written. `dcms pull` takes their changes in (markers where you both changed a file), then push again.");
                return false;
            }
            var saved = await Api.JsonAsync(res, ct);
            foreach (var (path, hash) in saved?["hashes"]?.AsObject() ?? []) hashes[path] = Api.Str(hash)!;
            foreach (var d in delete) hashes.Remove(Api.Str(d?["path"])!);
            return true;
        }
        foreach (var path in puts)
        {
            batch[path] = new JsonObject { ["content"] = local[path], ["baseHash"] = state.Hashes.GetValueOrDefault(path) };
            size += local[path].Length;
            if (size < PushBatchChars) continue;
            if (!await SendAsync(batch, [])) return Saved(1);
            batch = [];
            size = 0;
        }
        var deletions = new JsonArray([.. deleted.Select(p => (JsonNode?)new JsonObject { ["path"] = p, ["baseHash"] = state.Hashes[p] })]);
        if (!await SendAsync(batch, deletions)) return Saved(1);
        w.WriteLine($"Pushed {puts.Count} file(s), deleted {deleted.Count}, into your draft of {state.Name} ({state.Branch}). `dcms commit -m \"…\"` commits it.");
        return Saved(0);

        // What was written before a refusal stays written, and known.
        int Saved(int code)
        {
            WriteState(root, state with { Hashes = hashes });
            return code;
        }
    }

    internal async Task<int> CommitAsync(string dir, string message, string? resolve, bool resolved, TextWriter w, CancellationToken ct)
    {
        var root = FullDir(dir);
        var state = ReadState(root);
        if (resolved && state.Conflicts is not { Length: > 0 }) throw new CliException("There is nothing to resolve: commit without --resolved.");
        if (resolved)
        {
            var marked = state.Conflicts!.Where(p => File.Exists(Path.Combine(root, p)) && !Sync.IsBinary(p) && Sync.HasMarkers(File.ReadAllText(Path.Combine(root, p)))).ToList();
            if (marked.Count > 0) throw new CliException($"These still hold conflict markers: {string.Join(", ", marked)}.");
        }
        if (await PushAsync(dir, w, ct) != 0) return 1;
        state = ReadState(root);

        var body = new JsonObject { ["branch"] = state.Branch, ["message"] = message };
        if (resolve is not null) body["resolve"] = resolve == "ours" ? "mine" : "theirs";
        if (resolved)
        {
            var resolutions = new JsonObject();
            foreach (var p in state.Conflicts!)
            {
                var full = Path.Combine(root, p);
                resolutions[p] = File.Exists(full) ? Sync.ToServer(p, File.ReadAllBytes(full)) : null;
            }
            body["resolutions"] = resolutions;
        }
        using var res = await In(state, HttpMethod.Post, "/git/commit", body, ct);
        if (res.StatusCode == HttpStatusCode.Conflict)
        {
            var moved = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
            if (Api.Str(moved?["error"]) != "branch-moved") throw new CliException(Api.Str(moved?["error"]) ?? "The server refused the commit.");
            var conflicts = new List<string>();
            var files = new Dictionary<string, byte[]>();
            foreach (var f in moved?["files"]?.AsArray() ?? [])
            {
                var path = Api.Str(f?["path"])!;
                conflicts.Add(path);
                if (!Sync.IsBinary(path)) files[path] = Clone.Utf8(Sync.Marked(Api.Str(f?["draftContent"]), Api.Str(f?["branchContent"])));
            }
            Clone.WriteFiles(root, files);
            WriteState(root, state with { Conflicts = [.. conflicts] });
            w.WriteLine($"{state.Branch} moved since you pulled, and these changed on both sides: {string.Join(", ", conflicts)}.");
            w.WriteLine("The markers are in the files now. Edit them, then `dcms commit -m \"…\" --resolved`; or commit with --ours or --theirs.");
            return 2;
        }
        var done = await Api.JsonAsync(res, ct);
        w.WriteLine($"Committed {Api.Str(done?["sha"])?[..7]} to {Api.Str(done?["branch"])}.");
        WriteState(root, ReadState(root) with { Conflicts = null });
        // The draft now equals the branch's head, which may hold others' work: take it in.
        return await PullAsync(null, dir, null, null, null, w, ct) == 0 ? 0 : 2;
    }

    internal async Task<int> PublishAsync(string dir, TextWriter w, CancellationToken ct)
    {
        var root = FullDir(dir);
        var state = ReadState(root);
        var site = state.Workspace.StartsWith("sites/", StringComparison.Ordinal);
        using var res = site
            ? await In(state, HttpMethod.Post, $"/publish?branch={Uri.EscapeDataString(state.Branch)}", null, ct)
            : await In(state, HttpMethod.Post, "/git/merge", new JsonObject { ["head"] = state.Branch }, ct);
        var answer = await Api.JsonAsync(res, ct);
        if (answer?["conflict"]?.GetValue<bool>() == true)
        {
            foreach (var f in answer["files"]?.AsArray() ?? []) w.WriteLine($"conflict: {Api.Str(f?["path"])}");
            w.WriteLine($"{state.Branch} does not merge into release cleanly. Resolve it in the console, or merge release into {state.Branch}, then publish again.");
            return 2;
        }
        if (answer?["upToDate"]?.GetValue<bool>() == true || answer?["released"]?.GetValue<bool>() == false && answer["upToDate"] is not null)
        {
            w.WriteLine("release already has everything: nothing to publish.");
            return 0;
        }
        w.WriteLine(site
            ? $"Publishing {state.Name}: {Api.Str(answer?["sha"])?[..Math.Min(7, Api.Str(answer?["sha"])?.Length ?? 0)]} is building. `dcms builds --watch` follows it."
            : $"Merged {state.Branch} into release: a deployment is building. `dcms op call functions.deployments` lists it; deploy it with functions.deploy.");
        return 0;
    }

    internal async Task<int> BuildsAsync(string dir, bool watch, TextWriter w, CancellationToken ct)
    {
        var root = FullDir(dir);
        var state = ReadState(root);
        if (!state.Workspace.StartsWith("sites/", StringComparison.Ordinal)) throw new CliException("Builds are a site's. An app's deployments: `dcms op call functions.deployments`.");
        string? last = null;
        var until = DateTime.UtcNow.AddMinutes(15);
        while (true)
        {
            using var res = await In(state, HttpMethod.Get, "/builds?limit=5", null, ct);
            var builds = (await Api.JsonAsync(res, ct))?.AsArray() ?? [];
            var newest = builds.FirstOrDefault();
            var status = Api.Str(newest?["status"]);
            var line = newest is null ? "No builds yet."
                : $"{Api.Str(newest["shortSha"]) ?? "-"}  {status,-10} {Api.Str(newest["branch"])}  {Api.Str(newest["createdAt"])}"
                  + $"{(newest["active"]?.GetValue<bool>() == true ? "  (live)" : "")}{(Api.Str(newest["error"]) is { } e ? $"  {e}" : "")}";
            if (!watch)
            {
                foreach (var b in builds)
                {
                    w.WriteLine($"{Api.Str(b?["id"])}  {Api.Str(b?["shortSha"]) ?? "-"}  {Api.Str(b?["status"]),-10} {Api.Str(b?["branch"])}  {Api.Str(b?["createdAt"])}"
                                + $"{(b?["active"]?.GetValue<bool>() == true ? "  (live)" : "")}");
                }
                return 0;
            }
            if (line != last) w.WriteLine(line);
            last = line;
            if (status is not ("Queued" or "Building")) return status == "Failed" ? 1 : 0;
            if (DateTime.UtcNow > until) throw new CliException("Still building after 15 minutes. `dcms builds` shows it later.");
            await Task.Delay(3000, ct);
        }
    }

    private static readonly Dictionary<string, string> Modes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pages"] = "StaticPrerender",
        ["react-builder"] = "ReactBuilder",
        ["react-app"] = "ReactApp",
        ["files"] = "StaticFiles",
    };

    internal async Task<int> CreateSiteAsync(string name, string mode, string? template, string? dir, string tenant, string? environment, TextWriter w, CancellationToken ct)
    {
        var renderMode = Modes.GetValueOrDefault(mode) ?? (Modes.ContainsValue(mode) ? mode : throw new CliException($"--mode is one of {string.Join(", ", Modes.Keys)}."));
        var created = await api.CallInAsync("/api/admin/sites", tenant, environment, new JsonObject { ["name"] = name, ["renderMode"] = renderMode }, null, ct);
        var id = Api.Str(created?["id"]) ?? throw new CliException("The server made no site.");
        w.WriteLine($"Created {name} ({renderMode}): {id}");
        var state = new SyncState(config.ReadCredentials()?.Server ?? api.Agent?.Server ?? "", tenant, environment, $"sites/{id}", name, "main", null, []);
        if (renderMode != "StaticFiles" && template is not null)
        {
            var (_, draft) = await DraftAsync(state, ct);
            var starter = await api.CallInAsync($"/api/admin/sites/{id}/starter-files?template={Uri.EscapeDataString(template)}", tenant, environment, null, null, ct);
            var put = new JsonObject();
            foreach (var (path, content) in starter?["files"]?.AsObject() ?? [])
            {
                put[path] = new JsonObject { ["content"] = content?.DeepClone(), ["baseHash"] = draft.TryGetValue(path, out var had) ? Sync.Hash(had) : null };
            }
            using (var res = await In(state, HttpMethod.Patch, "/ide/files?branch=main", new JsonObject { ["put"] = put, ["delete"] = new JsonArray() }, ct))
            {
                await Api.JsonAsync(res, ct);
            }
            using (var res = await In(state, HttpMethod.Post, "/git/commit", new JsonObject { ["branch"] = "main", ["message"] = $"Start from the {template} template" }, ct))
            {
                await Api.JsonAsync(res, ct);
            }
            w.WriteLine($"Started it from the {template} template.");
        }
        return dir is null ? 0 : await PullAsync(id, dir, "main", tenant, environment, w, ct);
    }

    private void AddSyncCommands(RootCommand root)
    {
        var target = new Argument<string?>("site-or-app") { Description = "A site (its name or id) or a Functions app (its slug).", Arity = ArgumentArity.ZeroOrOne };
        var branch = new Option<string?>("--branch") { Description = "The branch; default: main (or the one pulled before)." };
        var pull = new Command("pull", "Pull a site's or app's files into a folder, or take the server's changes into one pulled before.")
        {
            target, DirOption, branch, WorkspaceOption, EnvironmentOption,
        };
        Act(pull, async (r, ct) =>
        {
            var named = r.GetValue(WorkspaceOption);
            return await PullAsync(r.GetValue(target), r.GetValue(DirOption)!, r.GetValue(branch), named, named is null ? null : r.GetValue(EnvironmentOption), stdout, ct);
        });
        root.Subcommands.Add(pull);

        var status = new Command("status", "What you changed in a pulled folder since you last pulled or pushed.") { DirOption };
        Act(status, (r, _) => Task.FromResult(Status(r.GetValue(DirOption)!, stdout)));
        root.Subcommands.Add(status);

        var only = new Argument<string?>("path") { Description = "Only this file.", Arity = ArgumentArity.ZeroOrOne };
        var diff = new Command("diff", "Your changes against your draft on the server, line by line (needs git).") { only, DirOption };
        Act(diff, async (r, ct) => await DiffAsync(r.GetValue(DirOption)!, r.GetValue(only), stdout, ct));
        root.Subcommands.Add(diff);

        var push = new Command("push", "Write your changes into your draft on the server (nothing is committed).") { DirOption };
        Act(push, async (r, ct) => await PushAsync(r.GetValue(DirOption)!, stdout, ct));
        root.Subcommands.Add(push);

        var message = new Option<string>("--message", "-m") { Description = "What the commit does.", Required = true };
        var ours = new Option<bool>("--ours") { Description = "On a conflict, keep your version of every file." };
        var theirs = new Option<bool>("--theirs") { Description = "On a conflict, keep the branch's version of every file." };
        var resolved = new Option<bool>("--resolved") { Description = "Commit the conflicts you resolved in the files." };
        var commit = new Command("commit", "Push, then commit your draft to its branch.") { message, ours, theirs, resolved, DirOption };
        Act(commit, async (r, ct) => await CommitAsync(r.GetValue(DirOption)!, r.GetValue(message)!,
            r.GetValue(ours) ? "ours" : r.GetValue(theirs) ? "theirs" : null, r.GetValue(resolved), stdout, ct));
        root.Subcommands.Add(commit);

        var publish = new Command("publish", "Publish a site's branch (it builds and goes live), or merge an app's into release.") { DirOption };
        Act(publish, async (r, ct) => await PublishAsync(r.GetValue(DirOption)!, stdout, ct));
        root.Subcommands.Add(publish);

        var watch = new Option<bool>("--watch") { Description = "Follow the newest build until it is done." };
        var builds = new Command("builds", "A pulled site's builds, newest first.") { watch, DirOption };
        Act(builds, async (r, ct) => await BuildsAsync(r.GetValue(DirOption)!, r.GetValue(watch), stdout, ct));
        root.Subcommands.Add(builds);

        var sites = new Command("sites", "Sites in a workspace.");
        var siteName = new Argument<string>("name");
        var mode = new Option<string>("--mode") { Description = "pages, react-builder, react-app or files.", DefaultValueFactory = _ => "react-builder" };
        var template = new Option<string?>("--template") { Description = "Start from: blank, content, landing or visual." };
        var into = new Option<string?>("--dir", "-d") { Description = "Pull it into this folder." };
        Add(sites, new Command("create", "Create a site, start it from a template, pull it.") { siteName, mode, template, into, WorkspaceOption, EnvironmentOption },
            async (r, ct) =>
            {
                var (tenant, environment) = Scope(r);
                return await CreateSiteAsync(r.GetValue(siteName)!, r.GetValue(mode)!, r.GetValue(template), r.GetValue(into), tenant, environment, stdout, ct);
            });
        root.Subcommands.Add(sites);
    }
}
