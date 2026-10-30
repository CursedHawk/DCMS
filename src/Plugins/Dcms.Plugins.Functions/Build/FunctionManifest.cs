using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dcms.Plugins.Functions.Api;

namespace Dcms.Plugins.Functions.Build;

public sealed record FunctionUses(
    IReadOnlyDictionary<string, IReadOnlyList<string>> Tables,
    IReadOnlyList<string> Contracts,
    IReadOnlyList<string> Egress,
    IReadOnlyList<string> Secrets,
    IReadOnlyList<string> Emits);

/// <summary>One function as a deployment holds it: what <c>defineFunction</c> was given, validated.</summary>
public sealed record FunctionSpec(
    string Name,
    string Runtime,
    string? Description,
    IReadOnlyList<JsonObject> Triggers,
    IReadOnlyList<string> Http,
    FunctionUses Uses,
    int? TimeoutMs,
    int? HeapMb);

public sealed record ManifestProblem(string Function, string Message);

/// <summary>
/// Reads and bounds what each function's <c>defineFunction</c> declared (ADR 0029). The
/// definition is the tenant's own data, produced by running its code once in the sandbox, so
/// everything here is checked like any other input: known keys only, shapes, sizes and counts.
/// It is also what the broker later trusts to say what a run may reach, so a typo is an error
/// here rather than a call refused in production.
/// </summary>
public static partial class FunctionManifest
{
    public const string Runtime = "deno-ts";
    public const int MaxFunctions = 200;

    private static readonly string[] Events = ["form.submitted", "visitor.registered", "user.invited", "user.activated"];
    private static readonly string[] Verbs = ["read", "create", "update", "delete"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,62}\.[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex TableName();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,127}$")]
    private static partial Regex Topic();

    [GeneratedRegex(@"^[a-z][a-z0-9.-]{0,63}@\d{1,3}$")]
    private static partial Regex ContractId();

    [GeneratedRegex(@"^(\*\.)?([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex Host();

    [GeneratedRegex(@"^[A-Z_][A-Z0-9_]{0,63}$")]
    private static partial Regex SecretName();

    [GeneratedRegex(@"^role:[A-Za-z0-9 _.-]{1,64}$")]
    private static partial Regex Role();

    [GeneratedRegex(@"^(\d{1,4})(m|h|d)$")]
    private static partial Regex Interval();

    [GeneratedRegex(@"^[0-9*,/\-]{1,64}$")]
    private static partial Regex CronField();

    /// <summary>The functions, or every problem found. Definitions are keyed by function name.</summary>
    public static (List<FunctionSpec> Functions, List<ManifestProblem> Problems) Read(IReadOnlyDictionary<string, JsonNode?> definitions)
    {
        var functions = new List<FunctionSpec>();
        var problems = new List<ManifestProblem>();
        if (definitions.Count > MaxFunctions)
        {
            problems.Add(new ManifestProblem("", $"A repository holds at most {MaxFunctions} functions."));
            return (functions, problems);
        }
        foreach (var (name, definition) in definitions.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            var found = new List<string>();
            var spec = ReadOne(name, definition as JsonObject ?? new JsonObject(), found);
            problems.AddRange(found.Select(p => new ManifestProblem(name, p)));
            if (found.Count == 0)
            {
                functions.Add(spec);
            }
        }
        return (functions, problems);
    }

    /// <summary>
    /// The permissions a deployment of <paramref name="functions"/> needs its deployer to hold
    /// (definer's rights): what its table verbs mean in Dynamic Apps, and the right to secrets a
    /// function could carry off. Contract operations are added where their calls are brokered.
    /// </summary>
    public static List<string> RequiredPermissions(IEnumerable<FunctionSpec> functions)
    {
        var required = new SortedSet<string>(StringComparer.Ordinal) { FunctionsPermissions.Deploy };
        foreach (var function in functions)
        {
            foreach (var verb in function.Uses.Tables.Values.SelectMany(v => v))
            {
                required.Add(verb switch
                {
                    "read" => "plugin:dynamic-apps:data-read",
                    "delete" => "plugin:dynamic-apps:data-delete",
                    _ => "plugin:dynamic-apps:data-write",
                });
            }
            if (function.Uses.Secrets.Count > 0)
            {
                required.Add(FunctionsPermissions.Secrets);
            }
            // A trigger hands the function what happened: reading it is a read like any other.
            foreach (var permission in function.Triggers.Select(TriggerPermission).OfType<string>())
            {
                required.Add(permission);
            }
        }
        return [.. required];
    }

    /// <summary>What a trigger delivers is someone's data: the permission it takes to read it, if any.</summary>
    public static string? TriggerPermission(JsonObject trigger) => trigger["kind"]?.GetValue<string>() switch
    {
        "row" or "relation" => "plugin:dynamic-apps:data-read",
        "event" => trigger["name"]?.GetValue<string>() switch
        {
            "form.submitted" => Dcms.Plugins.Forms.Api.FormsPermissions.SubmissionsRead,
            "user.invited" or "user.activated" => Dcms.Plugins.UserAuth.Api.UserAuthPermissions.UsersRead,
            "visitor.registered" => Dcms.Plugins.VisitorAuth.Api.VisitorPermissions.Read,
            _ => null,
        },
        _ => null,
    };

    /// <summary>Whether a live deployment was vouched for what <paramref name="trigger"/> delivers.</summary>
    public static bool MayReceive(JsonObject trigger, IReadOnlyCollection<string> vouched) =>
        TriggerPermission(trigger) is not { } permission || vouched.Contains(permission);

    public static string Serialize(IEnumerable<FunctionSpec> functions) =>
        JsonSerializer.Serialize(new { functions }, Json);

    public static IReadOnlyList<FunctionSpec> Deserialize(string manifest) =>
        JsonSerializer.Deserialize<ManifestDocument>(manifest, Json)?.Functions ?? [];

    private sealed record ManifestDocument(List<FunctionSpec> Functions);

    private static FunctionSpec ReadOne(string name, JsonObject definition, List<string> problems)
    {
        Known(definition, problems, "", "description", "triggers", "access", "uses", "limits", "handler");

        var description = Text(definition["description"], 500, "description", problems);

        var triggers = new List<JsonObject>();
        foreach (var node in Array(definition["triggers"], 20, "triggers", problems))
        {
            if (node is JsonObject trigger && Trigger(trigger, problems))
            {
                triggers.Add((JsonObject)trigger.DeepClone());
            }
        }

        var http = new List<string>();
        if (definition["access"] is JsonObject access)
        {
            Known(access, problems, "access.", "http");
            foreach (var who in Strings(access["http"], 10, "access.http", problems))
            {
                if (who is "public" or "user" or "apiKey" || Role().IsMatch(who))
                {
                    http.Add(who);
                }
                else
                {
                    problems.Add($"access.http: '{who}' is not \"public\", \"user\", \"apiKey\" or \"role:<name>\".");
                }
            }
        }
        else if (definition["access"] is not null)
        {
            problems.Add("access must be an object.");
        }

        var tables = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var (contracts, egress, secrets, emits) = (new List<string>(), new List<string>(), new List<string>(), new List<string>());
        if (definition["uses"] is JsonObject uses)
        {
            Known(uses, problems, "uses.", "tables", "contracts", "egress", "secrets", "messages");
            if (uses["tables"] is JsonObject declared)
            {
                if (declared.Count > 50) problems.Add("uses.tables: at most 50 tables.");
                foreach (var (table, verbs) in declared.Take(50))
                {
                    if (!TableName().IsMatch(table))
                    {
                        problems.Add($"uses.tables: '{table}' is not \"app.table\" (the app's address, then the table's API name).");
                        continue;
                    }
                    var allowed = Strings(verbs, 4, $"uses.tables.{table}", problems).Distinct().ToList();
                    if (allowed.FirstOrDefault(v => !Verbs.Contains(v)) is { } bad)
                    {
                        problems.Add($"uses.tables.{table}: '{bad}' is not read, create, update or delete.");
                        continue;
                    }
                    tables[table] = allowed;
                }
            }
            contracts.AddRange(Matching(uses["contracts"], 30, "uses.contracts", ContractId(), "\"name@major\"", problems));
            egress.AddRange(Matching(uses["egress"], 20, "uses.egress", Host(), "a host name such as api.example.com", problems)
                .Select(h => h.ToLowerInvariant()));
            secrets.AddRange(Matching(uses["secrets"], 50, "uses.secrets", SecretName(), "UPPER_SNAKE_CASE", problems));
            if (uses["messages"] is JsonObject messages)
            {
                Known(messages, problems, "uses.messages.", "emit");
                emits.AddRange(Matching(messages["emit"], 50, "uses.messages.emit", Topic(), "a topic such as orders.paid", problems));
            }
        }
        else if (definition["uses"] is not null)
        {
            problems.Add("uses must be an object.");
        }

        int? timeout = null, heap = null;
        if (definition["limits"] is JsonObject limits)
        {
            Known(limits, problems, "limits.", "timeoutMs", "heapMb");
            timeout = Number(limits["timeoutMs"], 100, 300_000, "limits.timeoutMs", problems);
            heap = Number(limits["heapMb"], 32, 256, "limits.heapMb", problems);
        }

        return new FunctionSpec(name, Runtime, description, triggers, http,
            new FunctionUses(tables, contracts.Distinct().ToList(), egress.Distinct().ToList(), secrets.Distinct().ToList(), emits.Distinct().ToList()),
            timeout, heap);
    }

    private static bool Trigger(JsonObject trigger, List<string> problems)
    {
        var kind = Text(trigger["kind"], 32, "trigger.kind", problems);
        var count = problems.Count;
        switch (kind)
        {
            case "row":
                Known(trigger, problems, "on.row: ", "kind", "table", "event", "fields");
                if (!TableName().IsMatch(Text(trigger["table"], 128, "on.row table", problems) ?? ""))
                    problems.Add("on.row: the table is \"app.table\" (the app's address, then the table's API name).");
                if (Text(trigger["event"], 16, "on.row event", problems) is not ("created" or "updated" or "deleted"))
                    problems.Add("on.row: the event is created, updated or deleted.");
                _ = Strings(trigger["fields"], 50, "on.row fields", problems);
                break;
            case "relation":
                Known(trigger, problems, "on.relation: ", "kind", "relationship", "event");
                if (!TableName().IsMatch(Text(trigger["relationship"], 128, "on.relation", problems) ?? ""))
                    problems.Add("on.relation: the relationship is \"app.relationship\".");
                if (Text(trigger["event"], 16, "on.relation event", problems) is not ("created" or "deleted"))
                    problems.Add("on.relation: the event is created or deleted.");
                break;
            case "message":
                Known(trigger, problems, "on.message: ", "kind", "topic");
                if (!Topic().IsMatch(Text(trigger["topic"], 128, "on.message", problems) ?? ""))
                    problems.Add("on.message: a topic is lowercase letters, digits, '.', '_' and '-', such as orders.paid.");
                break;
            case "schedule":
                Known(trigger, problems, "on.schedule: ", "kind", "cron");
                var expression = Text(trigger["cron"], 128, "on.schedule", problems) ?? "";
                var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 5 || !fields.All(f => CronField().IsMatch(f)) || Dcms.Plugins.Functions.Runtime.Cron.TryParse(expression) is not { } cron)
                    problems.Add("on.schedule: a cron expression has five fields: minute hour day month weekday.");
                else if (cron.MinGapMinutes() < 5)
                    problems.Add("on.schedule: it may fire at most every 5 minutes.");
                break;
            case "every":
                Known(trigger, problems, "on.every: ", "kind", "interval");
                var match = Interval().Match(Text(trigger["interval"], 16, "on.every", problems) ?? "");
                var minutes = match.Success ? int.Parse(match.Groups[1].Value) * (match.Groups[2].Value switch { "m" => 1, "h" => 60, _ => 1_440 }) : 0;
                if (minutes is < 5 or > 10_080)
                    problems.Add("on.every: an interval such as \"15m\", \"1h\" or \"1d\", between 5 minutes and 7 days.");
                break;
            case "event":
                Known(trigger, problems, "on.event: ", "kind", "name");
                if (!Events.Contains(Text(trigger["name"], 64, "on.event", problems)))
                    problems.Add($"on.event: one of {string.Join(", ", Events)}.");
                break;
            default:
                problems.Add($"'{kind}' is not a trigger: use on.row, on.relation, on.message, on.schedule, on.every or on.event.");
                break;
        }
        return problems.Count == count;
    }

    private static void Known(JsonObject node, List<string> problems, string where, params string[] keys)
    {
        foreach (var key in node.Select(p => p.Key).Where(k => !keys.Contains(k)))
        {
            problems.Add($"{where}'{key}' is not something a function declares.");
        }
    }

    private static string? Text(JsonNode? node, int max, string where, List<string> problems)
    {
        if (node is null) return null;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            problems.Add($"{where} must be text.");
            return null;
        }
        if (text.Length > max) problems.Add($"{where} is at most {max} characters.");
        return text;
    }

    private static int? Number(JsonNode? node, int min, int max, string where, List<string> problems)
    {
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<double>(out var n) && n >= min && n <= max && n == Math.Floor(n))
        {
            return (int)n;
        }
        problems.Add($"{where} is a whole number from {min} to {max}.");
        return null;
    }

    private static IEnumerable<JsonNode?> Array(JsonNode? node, int max, string where, List<string> problems)
    {
        if (node is null) return [];
        if (node is not JsonArray array)
        {
            problems.Add($"{where} must be a list.");
            return [];
        }
        if (array.Count > max) problems.Add($"{where}: at most {max}.");
        return array.Take(max);
    }

    private static IEnumerable<string> Strings(JsonNode? node, int max, string where, List<string> problems) =>
        Array(node, max, where, problems).Select(item => Text(item, 256, where, problems)).OfType<string>().ToList();

    private static IEnumerable<string> Matching(JsonNode? node, int max, string where, Regex pattern, string shape, List<string> problems)
    {
        var values = Strings(node, max, where, problems).ToList();
        foreach (var bad in values.Where(v => !pattern.IsMatch(v)))
        {
            problems.Add($"{where}: '{bad}' is not {shape}.");
        }
        return values.Where(v => pattern.IsMatch(v));
    }
}
