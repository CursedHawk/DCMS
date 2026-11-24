using System.CommandLine;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dcms.Cli;

/// <summary>
/// The operation catalog (ADR 0032, C3): every operation the server offers this person or agent,
/// listed, described and called generically. A route the server marks today is callable here
/// tomorrow without a new dcms.
/// </summary>
internal sealed partial class Commands
{
    /// <summary>Where a command acts: <c>--workspace</c> and <c>--env</c>, else the clone's link.</summary>
    private static readonly Option<string?> WorkspaceOption = new("--workspace", "-w") { Description = "The workspace (its slug); default: the linked clone's." };
    private static readonly Option<string?> EnvironmentOption = new("--env") { Description = "The environment; default: the linked clone's, else production." };

    private (string Tenant, string? Environment) Scope(ParseResult r)
    {
        if (r.GetValue(WorkspaceOption) is { Length: > 0 } workspace) return (workspace, r.GetValue(EnvironmentOption));
        var dir = Clone.Root(cwd);
        try
        {
            var link = Clone.ReadLink(config, dir);
            return (link.Tenant, r.GetValue(EnvironmentOption) ?? link.Environment);
        }
        catch (CliException)
        {
            throw new CliException("Which workspace? Pass --workspace <slug>, or run this in a linked clone.");
        }
    }

    private Command OpsList()
    {
        var search = new Argument<string?>("search") { Description = "Only operations whose name or summary contains this.", Arity = ArgumentArity.ZeroOrOne };
        var ops = new Command("ops", "What you may call here: every operation, by name, with its risk.") { search, WorkspaceOption, EnvironmentOption };
        Act(ops, async (r, ct) =>
        {
            var (tenant, environment) = Scope(r);
            var catalog = await CatalogAsync(tenant, environment, ct);
            var text = r.GetValue(search);
            foreach (var (name, risk, summary) in Entries(catalog).Where(e => text is null
                         || e.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || e.Summary.Contains(text, StringComparison.OrdinalIgnoreCase)))
            {
                stdout.WriteLine($"{name,-42} {risk,-9} {summary}");
            }
            return 0;
        });
        return ops;
    }

    private Command Op()
    {
        var op = new Command("op", "Describe or call one operation from `dcms ops`.");
        var name = new Argument<string>("name") { Description = "The operation, e.g. sites.list or plugins.users.directory@1.ListUsers." };
        var describe = new Command("describe", "An operation's method, path, parameters, body schema and risk.") { name, WorkspaceOption, EnvironmentOption };
        Add(op, describe, async (r, ct) =>
        {
            var (tenant, environment) = Scope(r);
            var found = Find(await CatalogAsync(tenant, environment, ct), r.GetValue(name)!);
            stdout.Write(Clone.Pretty(found.DeepClone()));
            return 0;
        });

        var path = new Option<string[]>("--path", "-p") { Description = "A path parameter: name=value. Repeat for more.", AllowMultipleArgumentsPerToken = true };
        var query = new Option<string[]>("--query", "-q") { Description = "A query parameter: name=value. Repeat for more.", AllowMultipleArgumentsPerToken = true };
        var body = new Option<string?>("--body", "-b") { Description = "The JSON body, or @file with it." };
        var file = new Option<string[]>("--file", "-f") { Description = "A file to upload: field=path (or just path for the first file field).", AllowMultipleArgumentsPerToken = true };
        var form = new Option<string[]>("--form") { Description = "Another multipart field: name=value.", AllowMultipleArgumentsPerToken = true };
        var instance = new Option<string?>("--instance") { Description = "A plugin operation's app (its slug), when several provide it." };
        var output = new Option<string?>("--out", "-o") { Description = "Write the answer to this file (for files and other bytes)." };
        var call = new Command("call", "Call an operation.") { name, path, query, body, file, form, instance, output, WorkspaceOption, EnvironmentOption };
        Add(op, call, async (r, ct) =>
        {
            var (tenant, environment) = Scope(r);
            var found = Find(await CatalogAsync(tenant, environment, ct), r.GetValue(name)!);
            var (verb, url, content) = Request(found, Pairs(r.GetValue(path)), Pairs(r.GetValue(query)), r.GetValue(body),
                r.GetValue(file) ?? [], Pairs(r.GetValue(form)), r.GetValue(instance));
            using var res = await api.RawAsync(verb, url, tenant, environment, content, null, ct);
            return await PrintAsync(res, r.GetValue(output), ct);
        });
        return op;
    }

    /// <summary>The catalog for this workspace and environment, kept per server and asked again with its ETag.</summary>
    private async Task<JsonNode> CatalogAsync(string tenant, string? environment, CancellationToken ct)
    {
        var server = config.ReadCredentials()?.Server ?? "";
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{server}|{tenant}|{environment}")))[..16];
        var cache = Path.Combine(config.Dir, "cache", $"catalog-{key}.json");
        JsonNode? cached = null;
        try
        {
            cached = File.Exists(cache) ? JsonNode.Parse(File.ReadAllText(cache)) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            // a broken cache is no cache
        }
        using var res = await api.RawAsync(HttpMethod.Get, "/api/admin/cli/catalog", tenant, environment, null, Api.Str(cached?["etag"]), ct);
        if (res.StatusCode == HttpStatusCode.NotModified && cached?["catalog"] is { } kept) return kept;
        var catalog = await Api.JsonAsync(res, ct) ?? throw new CliException("The server sent an empty catalog.");
        config.WritePrivate(cache, Clone.Utf8(new JsonObject { ["etag"] = res.Headers.ETag?.Tag, ["catalog"] = catalog.DeepClone() }.ToJsonString()));
        return catalog;
    }

    /// <summary>Routes by their name; contract operations as <c>plugins.{contract}.{operation}</c>.</summary>
    private static IEnumerable<(string Name, string Risk, string Summary)> Entries(JsonNode catalog)
    {
        foreach (var o in catalog["operations"]?.AsArray() ?? [])
        {
            yield return (Api.Str(o?["name"]) ?? "", Api.Str(o?["risk"]) ?? "", Api.Str(o?["summary"]) ?? "");
        }
        foreach (var c in catalog["contracts"]?.AsArray() ?? [])
        {
            var instances = c?["instances"]?.AsArray().Select(i => Api.Str(i?["slug"])).Where(s => s is not null).ToList() ?? [];
            foreach (var o in c?["operations"]?.AsArray() ?? [])
            {
                var summary = Api.Str(o?["description"]) ?? "";
                if (instances.Count > 0) summary += $" [apps: {string.Join(", ", instances)}]";
                yield return ($"plugins.{Api.Str(c?["id"])}.{Api.Str(o?["name"])}", RiskName(o?["risk"]), summary);
            }
        }
    }

    private static string RiskName(JsonNode? risk) => risk is JsonValue v && v.TryGetValue<int>(out var n)
        ? n switch { 0 => "Read", 1 => "Safe", _ => "Dangerous" }
        : Api.Str(risk) ?? "";

    /// <summary>One operation, as a route (<c>kind: route</c>) or a contract operation (<c>kind: contract</c>).</summary>
    private static JsonObject Find(JsonNode catalog, string name)
    {
        if (catalog["operations"]?.AsArray().FirstOrDefault(o => Api.Str(o?["name"]) == name) is { } route)
        {
            var found = route.DeepClone().AsObject();
            found["kind"] = "route";
            return found;
        }
        foreach (var c in catalog["contracts"]?.AsArray() ?? [])
        {
            foreach (var o in c?["operations"]?.AsArray() ?? [])
            {
                if ($"plugins.{Api.Str(c?["id"])}.{Api.Str(o?["name"])}" != name) continue;
                var found = o!.DeepClone().AsObject();
                found["kind"] = "contract";
                found["contract"] = Api.Str(c?["id"]);
                found["instances"] = c?["instances"]?.DeepClone();
                found["plane"] = Api.Str(catalog["plane"]);
                return found;
            }
        }
        throw new CliException($"There is no operation {name} here. `dcms ops` lists them.");
    }

    [GeneratedRegex(@"\{\*{0,2}([A-Za-z0-9_]+)(:[^}]*)?\??\}")]
    private static partial Regex RouteParameter();

    /// <summary>The request an operation and the given values make.</summary>
    internal static (HttpMethod Verb, string Url, HttpContent? Content) Request(JsonObject op, Dictionary<string, string> path,
        Dictionary<string, string> query, string? body, string[] files, Dictionary<string, string> form, string? instance)
    {
        HttpContent? Json(JsonNode? fallback = null)
        {
            var json = body is null ? fallback : Clone.ParseInput(body);
            return json is null ? null : new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
        }

        if (Api.Str(op["kind"]) == "contract")
        {
            var url = $"/api/admin/contracts/{Uri.EscapeDataString(Api.Str(op["contract"])!)}/{Uri.EscapeDataString(Api.Str(op["name"])!)}?plane={Api.Str(op["plane"]) ?? "admin"}";
            if (instance is not null) url += $"&instance={Uri.EscapeDataString(instance)}";
            return (HttpMethod.Post, url, Json(new JsonObject()));
        }

        var missing = new List<string>();
        var target = RouteParameter().Replace(Api.Str(op["path"])!, m =>
        {
            if (path.TryGetValue(m.Groups[1].Value, out var value))
            {
                // A catch-all ({**branch}) keeps its slashes.
                return m.Value.StartsWith("{**", StringComparison.Ordinal)
                    ? string.Join('/', value.Split('/').Select(Uri.EscapeDataString))
                    : Uri.EscapeDataString(value);
            }
            missing.Add(m.Groups[1].Value);
            return m.Value;
        });
        if (missing.Count > 0) throw new CliException($"Give --path {string.Join(" --path ", missing.Select(m => m + "=…"))}.");
        if (query.Count > 0) target += "?" + string.Join('&', query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
        var verb = new HttpMethod(Api.Str(op["method"]) ?? "GET");

        if (op["files"] is JsonArray fields && fields.Count > 0)
        {
            var multipart = new MultipartFormDataContent();
            foreach (var f in files)
            {
                var (field, filePath) = f.Contains('=') ? (f[..f.IndexOf('=')], f[(f.IndexOf('=') + 1)..]) : (Api.Str(fields[0])!, f);
                if (!File.Exists(filePath)) throw new CliException($"No such file: {filePath}");
                var part = new ByteArrayContent(File.ReadAllBytes(filePath));
                part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                multipart.Add(part, field, Path.GetFileName(filePath));
            }
            foreach (var (k, v) in form) multipart.Add(new StringContent(v), k);
            if (files.Length == 0) throw new CliException($"Give --file {Api.Str(fields[0])}=<path>.");
            return (verb, target, multipart);
        }
        return (verb, target, Json());
    }

    private static Dictionary<string, string> Pairs(string[]? values)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in values ?? [])
        {
            var at = v.IndexOf('=');
            if (at <= 0) throw new CliException($"{v}: expected name=value.");
            pairs[v[..at]] = v[(at + 1)..];
        }
        return pairs;
    }

    /// <summary>JSON pretty to stdout, other bytes to --out (or stdout), and the server's words on failure.</summary>
    private async Task<int> PrintAsync(HttpResponseMessage res, string? output, CancellationToken ct)
    {
        if (!res.IsSuccessStatusCode)
        {
            try
            {
                await Api.JsonAsync(res, ct);
            }
            catch (CliException e)
            {
                stderr.WriteLine(e.Message);
            }
            return 1;
        }
        if (res.StatusCode == HttpStatusCode.NoContent) return 0;
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        if (output is not null)
        {
            await File.WriteAllBytesAsync(output, bytes, ct);
            stderr.WriteLine($"Wrote {bytes.Length} bytes to {output}.");
            return 0;
        }
        if (res.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                stdout.Write(Clone.Pretty(JsonNode.Parse(bytes) ?? JsonValue.Create("")!));
                return 0;
            }
            catch (System.Text.Json.JsonException)
            {
                // not JSON after all: as text below
            }
        }
        stdout.Write(Encoding.UTF8.GetString(bytes));
        return 0;
    }
}
