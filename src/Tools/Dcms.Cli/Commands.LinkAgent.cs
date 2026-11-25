using System.CommandLine;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Dcms.Cli;

/// <summary>
/// <c>dcms link-agent</c> (ADR 0032, C4): one step from a signed-in person to a desktop agent
/// working in their workspace. It mints the agent its own token (with the person's sign-in, for one
/// workspace, some environments, perhaps reads only), keeps it in this machine's dcms folder,
/// points the agent's MCP configuration at <c>dcms mcp --agent &lt;id&gt;</c>, and gives it the
/// guide to DCMS as a skill. <c>dcms unlink-agent</c> takes all of it down again.
/// </summary>
internal sealed partial class Commands
{
    private Command LinkAgent()
    {
        var claude = new Option<bool>("--claude-code") { Description = "Claude Code (the default)." };
        var cursor = new Option<bool>("--cursor") { Description = "Cursor." };
        var codex = new Option<bool>("--codex") { Description = "OpenAI Codex CLI." };
        var generic = new Option<bool>("--generic") { Description = "Any other MCP client: prints the configuration." };
        var environments = new Option<string[]>("--env") { Description = "An environment it may reach; repeat for more. None: every one you reach.", AllowMultipleArgumentsPerToken = true };
        var readOnly = new Option<bool>("--read-only") { Description = "It may only read." };
        var name = new Option<string?>("--name") { Description = "What to call it; default: \"<agent> on <this machine>\"." };
        var days = new Option<int>("--days") { Description = "How long its token lasts (1-365).", DefaultValueFactory = _ => 90 };
        var user = new Option<bool>("--user") { Description = "For every project of yours, not just this folder (Claude Code, Cursor)." };
        var link = new Command("link-agent", "Connect a desktop AI agent to a workspace: its own token, its MCP server, the DCMS skill.")
        {
            claude, cursor, codex, generic, WorkspaceOption, environments, readOnly, name, days, user,
        };
        Act(link, async (r, ct) =>
        {
            var client = r.GetValue(cursor) ? "cursor" : r.GetValue(codex) ? "codex" : r.GetValue(generic) ? "generic" : "claude-code";
            var (workspace, _) = Scope(r);
            var server = config.ReadCredentials()?.Server ?? throw new CliException("Sign in first: dcms login");
            var label = r.GetValue(name) ?? $"{ClientName(client)} on {Environment.MachineName}";
            var envs = new JsonArray();
            foreach (var e in r.GetValue(environments) ?? []) envs.Add((JsonNode?)JsonValue.Create(e));

            var minted = await api.CallInAsync("/api/admin/me/agent-tokens", workspace, null, new JsonObject
            {
                ["name"] = label,
                ["environments"] = envs,
                ["readOnly"] = r.GetValue(readOnly),
                ["expiresInDays"] = r.GetValue(days),
            }, null, ct) ?? throw new CliException("The server minted no token.");
            var id = Api.Str(minted["id"])!;
            var serverName = $"dcms-{workspace}";
            var configPath = WriteAgentConfig(client, r.GetValue(user), serverName, id);
            var agent = new AgentLink(id, label, server, Api.Str(minted["token"])!, workspace,
                [.. envs.Select(e => (string)e!)], r.GetValue(readOnly), client, configPath, minted["expiresAt"]!.GetValue<DateTimeOffset>());
            config.WriteAgent(agent);

            var skill = await api.RawAsync(HttpMethod.Get, "/api/admin/cli/skill", workspace, null, null, null, ct);
            var skillPath = skill.IsSuccessStatusCode ? WriteSkill(client, r.GetValue(user), await skill.Content.ReadAsStringAsync(ct)) : null;
            if (skillPath is not null) config.WriteAgent(agent = agent with { SkillPath = skillPath });

            stdout.WriteLine($"Linked {label} to {workspace}{(agent.Environments.Length > 0 ? $" ({string.Join(", ", agent.Environments)})" : "")}"
                + $"{(agent.ReadOnly ? ", read only" : "")}, until {agent.ExpiresAt:yyyy-MM-dd}.");
            if (configPath is not null) stdout.WriteLine($"MCP server '{serverName}': {configPath}");
            if (skillPath is not null) stdout.WriteLine($"Skill: {skillPath}");
            stdout.WriteLine($"Restart {ClientName(client)} to pick it up. Revoke it any time: dcms unlink-agent {id}");
            return 0;
        });
        return link;
    }

    private Command UnlinkAgent()
    {
        var id = new Argument<string>("id") { Description = "The agent's id (dcms agents lists them)." };
        var unlink = new Command("unlink-agent", "Revoke an agent's token and remove its MCP configuration from this machine.") { id };
        Act(unlink, async (r, ct) =>
        {
            var agent = config.ReadAgent(r.GetValue(id)!);
            try
            {
                await api.CallAsync($"/api/admin/me/agent-tokens/{Uri.EscapeDataString(agent.Id)}", null, null, HttpMethod.Delete, ct);
            }
            catch (CliException e) when (e.Message.Contains("404", StringComparison.Ordinal))
            {
                // already gone on the server
            }
            RemoveAgentConfig(agent);
            File.Delete(config.AgentPath(agent.Id));
            // The skill too, unless another agent linked here still reads it.
            var skill = agent.SkillPath is { } path && File.Exists(path) && new FileInfo(path).LinkTarget is null
                && !config.ReadAgents().Any(a => a.SkillPath == path) ? path : null;
            if (skill is not null)
            {
                File.Delete(skill);
                var folder = Path.GetDirectoryName(skill)!;
                if (Path.GetFileName(folder) == "dcms" && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
            stdout.WriteLine($"Unlinked {agent.Name}: its token is revoked and its configuration{(skill is null ? "" : " and skill")} removed.");
            return 0;
        });
        return unlink;
    }

    private static string ClientName(string client) => client switch
    {
        "cursor" => "Cursor",
        "codex" => "Codex",
        "generic" => "your MCP client",
        _ => "Claude Code",
    };

    /// <summary>How the agent starts this command line: its own path, or `dcms` on PATH when run through `dotnet`.</summary>
    private static string SelfCommand() =>
        Environment.ProcessPath is { } self && !Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? self : "dcms";

    /// <summary>Points the agent's MCP configuration at <c>dcms mcp --agent id</c>; where, as <c>file#server</c> for unlinking.</summary>
    private string? WriteAgentConfig(string client, bool forUser, string serverName, string id)
    {
        var command = SelfCommand();
        var args = new JsonArray("mcp", "--agent", id);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        switch (client)
        {
            case "claude-code" when forUser:
                if (RunQuietly("claude", "mcp", "add", "--scope", "user", serverName, "--", command, "mcp", "--agent", id))
                {
                    return $"claude:user#{serverName}";
                }
                stdout.WriteLine($"Could not run `claude`. Add it yourself: claude mcp add --scope user {serverName} -- \"{command}\" mcp --agent {id}");
                return null;
            case "claude-code":
                return MergeJson(Path.Combine(cwd, ".mcp.json"), serverName, new JsonObject { ["type"] = "stdio", ["command"] = command, ["args"] = args });
            case "cursor":
                return MergeJson(forUser ? Path.Combine(home, ".cursor", "mcp.json") : Path.Combine(cwd, ".cursor", "mcp.json"),
                    serverName, new JsonObject { ["command"] = command, ["args"] = args });
            case "codex":
            {
                var path = Path.Combine(home, ".codex", "config.toml");
                var text = File.Exists(path) ? File.ReadAllText(path) : "";
                if (!text.Contains($"[mcp_servers.{serverName}]", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllText(path, $"{(text.Length > 0 && !text.EndsWith('\n') ? "\n" : "")}\n[mcp_servers.{serverName}]\n"
                        + $"command = \"{command.Replace("\\", "\\\\")}\"\nargs = [\"mcp\", \"--agent\", \"{id}\"]\n");
                }
                return $"{path}#{serverName}";
            }
            default:
                stdout.WriteLine(Clone.Pretty(new JsonObject
                {
                    ["mcpServers"] = new JsonObject { [serverName] = new JsonObject { ["command"] = command, ["args"] = args } },
                }));
                return null;
        }
    }

    /// <summary>Adds (or replaces) one server in an <c>mcpServers</c> JSON file, keeping everything else in it.</summary>
    internal static string MergeJson(string path, string serverName, JsonObject server)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new CliException($"Refusing to write {path}: it is a symbolic link.");
        var doc = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : [];
        if (doc["mcpServers"] is not JsonObject servers) doc["mcpServers"] = servers = [];
        servers[serverName] = server;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Clone.Pretty(doc));
        return $"{path}#{serverName}";
    }

    private void RemoveAgentConfig(AgentLink agent)
    {
        if (agent.ConfigPath is not { } at) return;
        var hash = at.LastIndexOf('#');
        if (hash < 0) return;
        var (path, serverName) = (at[..hash], at[(hash + 1)..]);
        if (path == "claude:user")
        {
            RunQuietly("claude", "mcp", "remove", "--scope", "user", serverName);
            return;
        }
        if (!File.Exists(path)) return;
        if (path.EndsWith(".toml", StringComparison.Ordinal))
        {
            // Our own section, up to the next one.
            var lines = File.ReadAllLines(path).ToList();
            var start = lines.FindIndex(l => l.Trim() == $"[mcp_servers.{serverName}]");
            if (start < 0) return;
            var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
            lines.RemoveRange(start, (end < 0 ? lines.Count : end) - start);
            File.WriteAllLines(path, lines);
            return;
        }
        if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject doc && doc["mcpServers"] is JsonObject servers && servers.Remove(serverName))
        {
            File.WriteAllText(path, Clone.Pretty(doc));
        }
    }

    /// <summary>The guide to DCMS where the agent reads its skills or rules; where it went.</summary>
    private string WriteSkill(string client, bool forUser, string markdown)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = client switch
        {
            "claude-code" => Path.Combine(forUser ? Path.Combine(home, ".claude") : Path.Combine(cwd, ".claude"), "skills", "dcms", "SKILL.md"),
            "cursor" => Path.Combine(forUser ? Path.Combine(home, ".cursor") : Path.Combine(cwd, ".cursor"), "rules", "dcms.mdc"),
            _ => Path.Combine(config.Dir, "skills", "dcms.md"),
        };
        if (new FileInfo(path).LinkTarget is not null) throw new CliException($"Refusing to write {path}: it is a symbolic link.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, markdown);
        return path;
    }

    private static bool RunQuietly(string file, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) start.ArgumentList.Add(a);
            using var p = Process.Start(start);
            if (p is null) return false;
            p.WaitForExit(30_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
