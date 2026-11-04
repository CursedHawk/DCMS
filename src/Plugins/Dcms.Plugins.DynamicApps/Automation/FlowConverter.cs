using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.DynamicApps.Api.Model;

namespace Dcms.Plugins.DynamicApps.Automation;

/// <summary>A flow the converter cannot write as a function, and why.</summary>
public sealed class FlowConversionException(string message) : Exception(message);

/// <summary>
/// Writes an app's flows as Functions code (ADR 0029, F11): one file per flow under
/// <c>functions/</c>, its trigger as the function's, its condition and steps as code. Expressions
/// become TypeScript through <c>lib/flow.ts</c>, which keeps the flow language's rules, so a
/// converted function does what its flow did. Each step's action becomes the SDK call it stood
/// for, and <c>uses</c> declares exactly what those calls reach.
/// </summary>
public static partial class FlowConverter
{
    public const string HelpersPath = "lib/flow.ts";

    /// <summary>The platform events flows could start on; functions take the same names (on.event).</summary>
    private static readonly HashSet<string> PlatformEvents = new(StringComparer.Ordinal)
    {
        "visitor.registered", "form.submitted", "user.invited", "user.activated",
    };

    /// <param name="UserAuth">The workspace's User Authentication app, for the user-auth actions.</param>
    /// <param name="VisitorAuth">Its Visitor accounts app, for the visitor actions.</param>
    public sealed record Providers(string? UserAuth = null, string? VisitorAuth = null);

    /// <param name="Functions">Each flow's function, by flow API name.</param>
    public sealed record Result(IReadOnlyDictionary<string, string> Files, IReadOnlyDictionary<string, string> Functions);

    /// <summary>The helpers every converted function imports.</summary>
    public static string Helpers()
    {
        using var stream = typeof(FlowConverter).Assembly.GetManifestResourceStream("FlowHelpers.ts")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <param name="taken">Function names already used in the repository; the new ones are added.</param>
    /// <exception cref="FlowConversionException">A flow uses something with no function equivalent.</exception>
    public static Result Convert(string app, AppConfig config, Providers providers, ISet<string> taken)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var flow in config.Flows)
        {
            names[flow.ApiName] = Unique(FunctionName(app, flow.ApiName), taken);
        }
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var flow in config.Flows)
        {
            new Writer(app, config, providers, flow, names, files).Write();
        }
        return new Result(files, names);
    }

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex NotNameChars();

    private static string FunctionName(string app, string flow)
    {
        var name = NotNameChars().Replace($"{app}-{flow}".ToLowerInvariant().Replace('_', '-'), "-").Trim('-');
        name = Regex.Replace(name, "-{2,}", "-");
        if (name.Length == 0 || !char.IsAsciiLetterLower(name[0])) name = "flow-" + name;
        return name.Length > 56 ? name[..56].TrimEnd('-') : name;
    }

    private static string Unique(string name, ISet<string> taken)
    {
        var candidate = name;
        for (var i = 2; !taken.Add(candidate); i++) candidate = $"{name}-{i}";
        return candidate;
    }

    private sealed class Writer(
        string app, AppConfig config, Providers providers, FlowDef flow, IReadOnlyDictionary<string, string> names, Dictionary<string, string> files)
    {
        private readonly SortedDictionary<string, SortedSet<string>> _tables = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _contracts = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _emits = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _links = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _notifications = new(StringComparer.Ordinal);
        private readonly List<string> _imports = [];
        private bool _email, _content;
        private readonly StringBuilder _body = new();

        private string Name => names[flow.ApiName];

        public void Write()
        {
            var (trigger, kind) = Trigger();
            foreach (var step in flow.Steps)
            {
                _body.Append($"\n  // {step.Id}: {step.Action}\n");
                var call = Step(step);
                if (step.Condition is { Length: > 0 } condition)
                {
                    _body.Append($"  if ($.truthy({Expr(condition)})) {{\n    steps[{Str(step.Id)}] = {call};\n  }}\n");
                }
                else
                {
                    _body.Append($"  steps[{Str(step.Id)}] = {call};\n");
                }
            }

            var code = new StringBuilder();
            code.Append("// @ts-nocheck -- converted from a Dynamic Apps flow; remove this line to type-check it as you edit.\n");
            // The name as a JSON string: no character of it can end the comment (U+2028 is a line end in JavaScript).
            code.Append($"// Flow {Str(flow.DisplayName)} ({app}/{flow.ApiName}), written as a function when flows were retired.\n");
            code.Append("import { defineFunction, on } from \"@dcms/functions\";\n");
            code.Append("import * as $ from \"../lib/flow.ts\";\n");
            foreach (var import in _imports) code.Append(import).Append('\n');
            code.Append("\nexport default defineFunction({\n");
            var description = flow.Description is { Length: > 0 } d ? d : flow.DisplayName;
            if (description.Length > 0) code.Append($"  description: {Str(description.Length > 500 ? description[..500] : description)},\n");
            code.Append(flow.Enabled ? $"  triggers: [{trigger}],\n" : "  // The flow was switched off: so is its trigger.\n  triggers: [],\n");
            code.Append($"  uses: {Uses()},\n");
            code.Append("  async handler(input, ctx) {\n");
            code.Append($"  const {{ event, row, previous, changedFields, input: flowInput, run }} = $.scope({Str(kind.Kind)}, {Str(kind.Event)}, input, ctx);\n");
            code.Append("  const steps = {};\n");
            if (flow.Condition is { Length: > 0 } test)
            {
                code.Append($"  if (!$.truthy({Expr(test)})) return null; // the flow's condition\n");
            }
            code.Append(_body);
            code.Append("  return steps;\n  },\n});\n");
            files[$"functions/{Name}.ts"] = code.ToString();
        }

        private sealed record Started(string Kind, string Event);

        private (string Code, Started Kind) Trigger()
        {
            var t = flow.Trigger;
            switch (t.Event)
            {
                case AppEvent.RowCreated or AppEvent.RowUpdated or AppEvent.RowDeleted:
                {
                    var table = TableOf(t.TableId) ?? throw new FlowConversionException($"flow '{flow.ApiName}': its trigger's table is gone.");
                    var verb = t.Event["row.".Length..];
                    var fields = t.ChangedFields.Select(id => table.Fields.FirstOrDefault(f => f.Id == id)?.ApiName
                        ?? config.Relationships.FirstOrDefault(r => r.Id == id && r.SourceTableId == table.Id)?.ApiName).OfType<string>().ToList();
                    var options = verb == "updated" && fields.Count > 0 ? $"{{ fields: [{string.Join(", ", fields.Select(Str))}] }}" : "";
                    return ($"on.row({Str($"{app}.{table.ApiName}")}).{verb}({options})", new Started("row", t.Event));
                }
                case "relation.created" or "relation.deleted":
                {
                    var verb = t.Event["relation.".Length..];
                    var relationships = t.RelationshipId is { } id ? config.Relationships.Where(r => r.Id == id).ToList() : config.Relationships.ToList();
                    if (relationships.Count == 0) throw new FlowConversionException($"flow '{flow.ApiName}': its trigger's relationship is gone.");
                    return (string.Join(", ", relationships.Select(r => $"on.relation({Str($"{app}.{r.ApiName}")}).{verb}()")), new Started("relation", t.Event));
                }
                case AppEvent.RevisionPublished:
                    return ($"on.app({Str(app)}).published()", new Started("app", t.Event));
                case "schedule":
                    return ($"on.every({Str(Interval(Math.Clamp(t.EveryMinutes ?? 60, 5, 7 * 24 * 60)))})",
                        new Started("schedule", "schedule"));
                case "manual":
                    // Started by a person, or by another flow: now jobs.enqueue, functions.invoke or a manual run.
                    return ("", new Started("manual", "manual"));
                case var e when e.StartsWith(AppEvent.FlowEventPrefix, StringComparison.Ordinal):
                    return ($"on.message({Str(Topic(e[AppEvent.FlowEventPrefix.Length..]))})", new Started("message", e));
                case var e when PlatformEvents.Contains(e):
                    return ($"on.event({Str(e)})", new Started("event", e));
                default:
                    throw new FlowConversionException($"flow '{flow.ApiName}': functions have no trigger like '{t.Event}'.");
            }
        }

        private string Topic(string name) => $"{app}.{name}";

        /// <summary>The largest whole unit: on.every takes at most four digits (a week is "7d", not "10080m").</summary>
        private static string Interval(int minutes) =>
            minutes % 1440 == 0 ? $"{minutes / 1440}d" : minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes}m";

        private TableDef? TableOf(Guid? id) => config.Tables.FirstOrDefault(t => t.Id == id);

        // ---------------------------------------------------------------- steps

        private string Step(FlowStep step)
        {
            var input = step.Input;
            string In(string key, bool html = false) => input.TryGetPropertyValue(key, out var v) ? Template(v, html) : "undefined";
            bool Has(string key) => input.ContainsKey(key);
            switch (step.Action)
            {
                case "records.create@1":
                    return $"await {Table(input["table"], "create")}.create({In("values")})";
                case "records.update@1":
                    return $"await {Table(input["table"], "update")}.update({In("id")}, {In("values")})";
                case "records.delete@1":
                    return $"{{ deleted: await {Table(input["table"], "delete")}.delete({In("id")}) }}";
                case "records.lookup@1":
                    return Has("id")
                        ? $"await {Table(input["table"], "read")}.get({In("id")})"
                        : $"$.first(await {Table(input["table"], "read")}.find({{ filter: {In("filter")}, sort: {In("sort")}, pageSize: 1 }}))";
                case "records.query@1":
                    return $"$.page(await {Table(input["table"], "read")}.find({{ filter: {In("filter")}, sort: {In("sort")}, pageSize: {(Has("pageSize") ? In("pageSize") : "20")} }}))";
                case "environment.records.query@1":
                    return $"await {Linked(input)}.find({In("table")}, {{ filter: {In("filter")}, sort: {In("sort")}, pageSize: {(Has("pageSize") ? In("pageSize") : "20")} }})";
                case "environment.records.create@1":
                    return $"await {Linked(input)}.create({In("table")}, {In("values")})";
                case "environment.records.update@1":
                    return $"await {Linked(input)}.update({In("table")}, {In("id")}, {In("values")})";
                case "environment.records.delete@1":
                    return $"(await {Linked(input)}.delete({In("table")}, {In("id")}), {{ deleted: true }})";
                case "environment.contract.call@1":
                    Link(input);
                    return $"await ctx.links.call({In("environment")}, {In("app")}, {In("contract")}, {In("operation")}, {In("input")})";
                case "flow.invoke@1":
                    return $"{{ runId: (await ctx.jobs.enqueue({Invoked(input["flow"])}, {In("input")} ?? {{}})).id }}";
                case "event.publish@1":
                    return $"(await ctx.messages.emit({Published(input["name"])}, {In("payload")} ?? {{}}), {{ eventName: $.t`{AppEvent.FlowEventPrefix}${{{In("name")}}}` }})";
                case "dcms.email.send@1":
                    _email = true;
                    return $"await ctx.email.send({{ to: {In("to")}, subject: {In("subject")}, text: {In("text")}, html: {In("html", html: true)}, replyTo: {In("replyTo")} }})";
                case "dcms.notifications.raise@1":
                    _notifications.Add(DynamicAppsPermissions.DataRead);
                    return $"(await ctx.notifications.raise({{ title: {In("title")}, body: {In("body")}, severity: {In("severity")}, "
                           + $"linkPath: {(Has("linkPath") ? In("linkPath") : Str($"/apps/{app}"))}, audience: {Str(DynamicAppsPermissions.DataRead)} }}), {{}})";
                case "content.get@1":
                    _content = true;
                    return $"await ctx.content.get({In("contentType")}, {In("slug")}, {{ instanceId: {In("instanceId")} }})";
                case "content.list@1":
                    _content = true;
                    return $"await ctx.content.list({In("contentType")}, {{ page: {In("page")}, pageSize: {In("pageSize")}, instanceId: {In("instanceId")} }})";
                case "visitor.lookup@1":
                    return $"await {Visitors()}.get({{ visitorId: {In("visitorId")} }})";
                case "visitor-auth.set-attributes@1":
                    return $"await {Visitors()}.setAttributes({{ visitorId: {In("visitorId")}, attributes: {In("attributes")} }})";
                case "user-auth.invite@1":
                    return $"await $.invite({Directory()}, {Template(input, html: false)})";
                case "user-auth.add-to-group@1" or "user-auth.remove-from-group@1":
                    return $"await $.membership({Directory()}, {(step.Action.StartsWith("user-auth.add", StringComparison.Ordinal) ? "true" : "false")}, {Template(input, html: false)})";
                case "script.run@1":
                    return Script(step);
                default:
                    throw new FlowConversionException($"flow '{flow.ApiName}', step '{step.Id}': functions have no equivalent of {step.Action}.");
            }
        }

        /// <summary>A table of this app: <c>ctx.db.app.table</c>, or any of its tables when the flow chose one at run time.</summary>
        private string Table(JsonNode? table, string verb)
        {
            if (Literal(table) is { } name)
            {
                var known = config.Tables.FirstOrDefault(t => t.ApiName == name)
                            ?? throw new FlowConversionException($"flow '{flow.ApiName}': there is no table '{name}'.");
                Grant(known.ApiName, verb);
                return $"ctx.db[{Str(app)}][{Str(known.ApiName)}]";
            }
            foreach (var t in config.Tables) Grant(t.ApiName, verb);
            return $"ctx.db[{Str(app)}][{Template(table, html: false)}]";
        }

        private void Grant(string table, string verb)
        {
            var key = $"{app}.{table}";
            if (!_tables.TryGetValue(key, out var verbs)) _tables[key] = verbs = new SortedSet<string>(StringComparer.Ordinal);
            verbs.Add(verb);
        }

        private string Linked(JsonObject input)
        {
            Link(input);
            return $"ctx.links.records({Template(input["environment"], false)}, {Template(input["app"], false)})";
        }

        private void Link(JsonObject input)
        {
            if (Literal(input["environment"]) is not { } environment || Literal(input["app"]) is not { } other)
            {
                throw new FlowConversionException($"flow '{flow.ApiName}': a link's environment and app must be written out, not computed.");
            }
            _links.Add($"{environment}/{other}");
        }

        private string Invoked(JsonNode? target)
        {
            if (Literal(target) is { } name)
            {
                return Str(names.TryGetValue(name, out var function) ? function
                    : throw new FlowConversionException($"flow '{flow.ApiName}': it starts flow '{name}', which this app does not have."));
            }
            // Chosen at run time: the same names, as functions.
            var map = string.Join(", ", names.Select(n => $"{Str(n.Key)}: {Str(n.Value)}"));
            return $"({{ {map} }})[{Template(target, false)}]";
        }

        private string Published(JsonNode? name)
        {
            if (Literal(name) is { } literal)
            {
                _emits.Add(Topic(literal));
                return Str(Topic(literal));
            }
            // Chosen at run time: any topic one of this app's flows listens to.
            foreach (var f in config.Flows.Where(f => f.Trigger.Event.StartsWith(AppEvent.FlowEventPrefix, StringComparison.Ordinal)))
            {
                _emits.Add(Topic(f.Trigger.Event[AppEvent.FlowEventPrefix.Length..]));
            }
            return $"$.t`{app}.${{{Template(name, false)}}}`";
        }

        private string Visitors()
        {
            var slug = providers.VisitorAuth ?? throw new FlowConversionException($"flow '{flow.ApiName}': it uses visitor accounts, which this environment does not have.");
            _contracts.Add("visitors.profiles@1");
            return $"ctx.plugins[{Str(slug)}].visitorsProfiles";
        }

        private string Directory()
        {
            var slug = providers.UserAuth ?? throw new FlowConversionException($"flow '{flow.ApiName}': it uses User Authentication, which this environment does not have.");
            _contracts.Add("users.directory@1");
            return $"ctx.plugins[{Str(slug)}].usersDirectory";
        }

        /// <summary>A script step: its module, as written, beside the function; called with what the flow gave it.</summary>
        private string Script(FlowStep step)
        {
            var code = step.Input["code"] is JsonValue v && v.TryGetValue<string>(out var c) ? c : "";
            var path = $"lib/scripts/{Name}-{NotNameChars().Replace(step.Id.ToLowerInvariant().Replace('_', '-'), "-")}.ts";
            files[path] = "// @ts-nocheck -- a Dynamic Apps flow's script step, as it was written.\n" + code;
            var local = $"script_{_imports.Count}";
            _imports.Add($"import {local} from \"../{path}\";");
            var args = step.Input.TryGetPropertyValue("args", out var a) ? Template(a, false) : "{}";
            return $"await {local}(structuredClone({{ event, row, previous, changedFields, input: flowInput, steps, run, args: {args} }}))";
        }

        private string Uses()
        {
            var parts = new List<string>();
            if (_tables.Count > 0)
            {
                parts.Add($"tables: {{ {string.Join(", ", _tables.Select(t => $"{Str(t.Key)}: [{string.Join(", ", t.Value.Select(Str))}]"))} }}");
            }
            if (_contracts.Count > 0) parts.Add($"contracts: [{string.Join(", ", _contracts.Select(Str))}]");
            if (_emits.Count > 0) parts.Add($"messages: {{ emit: [{string.Join(", ", _emits.Select(Str))}] }}");
            if (_email) parts.Add("email: true");
            if (_notifications.Count > 0) parts.Add($"notifications: [{string.Join(", ", _notifications.Select(Str))}]");
            if (_content) parts.Add("content: true");
            if (_links.Count > 0) parts.Add($"links: [{string.Join(", ", _links.Select(Str))}]");
            return parts.Count == 0 ? "{}" : $"{{ {string.Join(", ", parts)} }}";
        }

        // ---------------------------------------------------------------- templates and expressions

        /// <summary>A step input: objects and lists as literals, strings with <c>{{ }}</c> holes as code.</summary>
        private string Template(JsonNode? node, bool html) => node switch
        {
            JsonObject o => o.Count == 0 ? "{}" : $"{{ {string.Join(", ", o.Select(p => $"{Str(p.Key)}: {Template(p.Value, html)}"))} }}",
            JsonArray a => $"[{string.Join(", ", a.Select(i => Template(i, html)))}]",
            JsonValue v when v.TryGetValue<string>(out var s) && s.Contains("{{", StringComparison.Ordinal) => Holes(s, html),
            null => "null",
            _ => node.ToJsonString(),
        };

        private string Holes(string text, bool html)
        {
            var holes = new List<(int Start, int End, string Expression)>();
            for (var at = 0; (at = text.IndexOf("{{", at, StringComparison.Ordinal)) >= 0;)
            {
                var end = text.IndexOf("}}", at + 2, StringComparison.Ordinal);
                if (end < 0) throw new FlowConversionException($"flow '{flow.ApiName}': a '{{{{' is not closed.");
                holes.Add((at, end + 2, text[(at + 2)..end].Trim()));
                at = end + 2;
            }
            if (!html && holes is [var only] && only.Start == 0 && only.End == text.Length)
            {
                return Expr(only.Expression);
            }
            var code = new StringBuilder(html ? "$.h`" : "$.t`");
            var from = 0;
            foreach (var (start, end, expression) in holes)
            {
                code.Append(Backticked(text[from..start])).Append("${").Append(Expr(expression)).Append('}');
                from = end;
            }
            return code.Append(Backticked(text[from..])).Append('`').ToString();
        }

        private string Expr(string source)
        {
            try
            {
                return Ts(Expressions.Parse(source));
            }
            catch (ExpressionException e)
            {
                throw new FlowConversionException($"flow '{flow.ApiName}': {e.Message} in \"{source}\".");
            }
        }

        private static readonly Dictionary<string, string> Operators = new(StringComparer.Ordinal)
        {
            ["=="] = "eq", ["!="] = "ne", ["in"] = "isIn", ["+"] = "add", ["-"] = "sub", ["*"] = "mul", ["/"] = "div", ["%"] = "mod",
            ["<"] = "lt", ["<="] = "le", [">"] = "gt", [">="] = "ge",
        };

        /// <summary>Calls the flow language's built-ins by their names in lib/flow.ts (two are renamed off JavaScript's).</summary>
        private static string Builtin(string name) => name switch { "string" => "str", "number" => "num", _ => name };

        private string Ts(Expressions.Node node) => node switch
        {
            Expressions.Literal l => l.Value is null ? "null" : l.Value.ToJsonString(),
            Expressions.Var v => v.Name == "input" ? "flowInput" : v.Name,
            Expressions.Member m => $"$.get({Ts(m.Target)}, {Str(m.Name)})",
            Expressions.Index i => $"$.at({Ts(i.Target)}, {Ts(i.Key)})",
            Expressions.ListLit l => $"$.list({string.Join(", ", l.Items.Select(Ts))})",
            Expressions.Unary { Op: "!" } u => $"!$.truthy({Ts(u.Operand)})",
            Expressions.Unary u => $"$.neg({Ts(u.Operand)})",
            Expressions.Conditional c => $"($.truthy({Ts(c.Test)}) ? {Ts(c.Then)} : {Ts(c.Else)})",
            Expressions.Binary { Op: "&&" } b => $"($.truthy({Ts(b.Left)}) && $.truthy({Ts(b.Right)}))",
            Expressions.Binary { Op: "||" } b => $"($.truthy({Ts(b.Left)}) || $.truthy({Ts(b.Right)}))",
            Expressions.Binary b => $"$.{Operators[b.Op]}({Ts(b.Left)}, {Ts(b.Right)})",
            Expressions.Call c => $"$.{Builtin(c.Name)}({string.Join(", ", c.Args.Select(Ts))})",
            _ => throw new FlowConversionException($"flow '{flow.ApiName}': an expression the converter does not know."),
        };

        private static string? Literal(JsonNode? node) =>
            node is JsonValue v && v.TryGetValue<string>(out var s) && !s.Contains("{{", StringComparison.Ordinal) ? s : null;

        private static string Str(string text) => JsonSerializer.Serialize(text);

        private static string Backticked(string text) => text.Replace("\\", "\\\\").Replace("`", "\\`").Replace("${", "\\${");
    }
}
