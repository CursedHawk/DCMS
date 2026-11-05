using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dcms.Functions.Compilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;

// dcms-csc: compiles a Functions repository's C# (ADR 0030). script-runner starts it per build
// through Confined (prlimit, no environment) because Roslyn on hostile input is a memory and CPU
// risk better kept out of the runner itself.
//
// stdin:  {"files":{"path":"text",...},"sdk":"<the generated Sdk.g.cs>"}
// stdout: {"ok":bool,"diagnostics":[{message,file,line,column,code}],"assembly":"<base64>","pdb":"<base64>",
//          "functions":[{"name","class","file","definition"}]}
//
// A definition has the shape a TypeScript function's describe pass gives (triggers, access, uses,
// limits, description), read from the attributes, so the host validates both languages alike.

var request = JsonNode.Parse(Console.OpenStandardInput()) as JsonObject ?? [];
var files = (request["files"] as JsonObject ?? [])
    .Where(f => FunctionCompilation.IsSource(f.Key) && f.Value is JsonValue)
    .ToDictionary(f => f.Key, f => f.Value!.GetValue<string>(), StringComparer.Ordinal);

var diagnostics = new JsonArray();
var output = new JsonObject { ["diagnostics"] = diagnostics, ["functions"] = new JsonArray() };
if (files.Count == 0)
{
    output["ok"] = true;
    Console.Out.Write(output.ToJsonString());
    return;
}

var trees = files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => FunctionCompilation.Parse(f.Key, f.Value)).ToList();
trees.Add(FunctionCompilation.Parse(FunctionCompilation.SdkPath, SdkPruning.Prune(request["sdk"]?.GetValue<string>() ?? "", trees)));
trees.Add(FunctionCompilation.Parse(FunctionCompilation.UsingsPath, FunctionCompilation.GlobalUsings));

var refs = Path.Combine(AppContext.BaseDirectory, "refs");
var curated = FunctionCompilation.Curated(File.ReadAllText(Path.Combine(refs, "CuratedAssemblies.txt")));
var references = Directory.GetFiles(refs, "*.dll")
    .Where(file => curated.Contains(Path.GetFileNameWithoutExtension(file)))
    .Select(file => MetadataReference.CreateFromFile(file))
    .ToList();
var compilation = FunctionCompilation.Create(trees, references);

void Report(string message, Location? location = null, string? code = null)
{
    var span = location?.GetLineSpan();
    var file = span?.Path is { Length: > 0 } p && files.ContainsKey(p) ? p : null;
    diagnostics.Add(new JsonObject
    {
        ["message"] = message,
        ["file"] = file,
        ["line"] = file is null ? null : span!.Value.StartLinePosition.Line + 1,
        ["column"] = file is null ? null : span!.Value.StartLinePosition.Character + 1,
        ["code"] = code,
    });
}

foreach (var d in compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Take(200))
{
    Report(d.GetMessage(System.Globalization.CultureInfo.InvariantCulture) + SdkPruning.Hint(d), d.Location, d.Id);
}

// Native interop. The sandbox would contain it anyway (there is no native library to reach but
// .NET's own, in WebAssembly memory), but a function has no business there.
string[] refused = ["System.Runtime.InteropServices.DllImportAttribute", "System.Runtime.InteropServices.LibraryImportAttribute",
                    "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute"];
foreach (var symbol in compilation.GetSymbolsWithName(_ => true, SymbolFilter.Member).OfType<IMethodSymbol>())
{
    if (symbol.GetAttributes().FirstOrDefault(a => refused.Contains(a.AttributeClass?.ToDisplayString())) is { } attribute)
    {
        Report($"{attribute.AttributeClass!.Name.Replace("Attribute", "", StringComparison.Ordinal)}: a function cannot call native code.",
            attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation());
    }
}

var functions = (JsonArray)output["functions"]!;
var names = new HashSet<string>(StringComparer.Ordinal);
var baseType = compilation.GetTypeByMetadataName("Dcms.Functions.Function`2");
foreach (var type in compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type).OfType<INamedTypeSymbol>())
{
    var function = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "Dcms.Functions.FunctionAttribute");
    if (function is null) continue;
    var location = type.Locations.FirstOrDefault();
    var own = function.ConstructorArguments.FirstOrDefault().Value as string ?? "";
    if (!Regex.IsMatch(own, "^[a-z][a-z0-9-]{0,62}$"))
    {
        Report($"[Function(\"{own}\")]: a function's name is lowercase letters, digits and dashes, starting with a letter.", location);
        continue;
    }
    // Its folder under functions/ comes first, as for TypeScript: [Function("refund")] in
    // functions/orders/Refund.cs is "orders/refund", called at /api/{app}/fn/orders/refund.
    var file = location?.GetLineSpan().Path ?? "";
    var folder = file.StartsWith("functions/", StringComparison.Ordinal) ? Path.GetDirectoryName(file[10..])?.Replace('\\', '/') ?? "" : "";
    var name = folder.Length == 0 ? own : $"{folder}/{own}";
    if (!Regex.IsMatch(name, "^(?=.{1,64}$)[a-z][a-z0-9-]*(?:/[a-z][a-z0-9-]*){0,3}$"))
    {
        Report($"'{name}': a function's folders under functions/ are part of its name, so they are lowercase letters, digits and dashes too, at most four deep and 64 characters in all.", location);
        continue;
    }
    if (!names.Add(name))
    {
        Report($"Two classes are function '{name}'.", location);
        continue;
    }
    if (type.ContainingType is not null || type.IsAbstract || type.IsGenericType || type.DeclaredAccessibility != Accessibility.Public
        || !Derives(type, baseType)
        || !type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
    {
        Report($"{type.Name}: a function is a public, non-abstract, top-level class deriving from Function<TIn, TOut>, with a public parameterless constructor.", location);
        continue;
    }
    functions.Add(new JsonObject
    {
        ["name"] = name,
        ["class"] = type.ContainingNamespace.IsGlobalNamespace ? type.MetadataName : $"{type.ContainingNamespace.ToDisplayString()}.{type.MetadataName}",
        ["file"] = location?.GetLineSpan().Path,
        ["definition"] = Definition(type, function),
    });
}

if (diagnostics.Count == 0)
{
    using var assembly = new MemoryStream();
    using var pdb = new MemoryStream();
    var emitted = compilation.Emit(assembly, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
    foreach (var d in emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
    {
        Report(d.GetMessage(System.Globalization.CultureInfo.InvariantCulture), d.Location, d.Id);
    }
    if (emitted.Success)
    {
        output["assembly"] = Convert.ToBase64String(assembly.ToArray());
        output["pdb"] = Convert.ToBase64String(pdb.ToArray());
    }
}
output["ok"] = diagnostics.Count == 0;
Console.Out.Write(output.ToJsonString());
return;

static bool Derives(INamedTypeSymbol type, INamedTypeSymbol? baseType)
{
    for (var t = type.BaseType; t is not null; t = t.BaseType)
    {
        if (SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, baseType)) return true;
    }
    return false;
}

static JsonObject Definition(INamedTypeSymbol type, AttributeData function)
{
    var definition = new JsonObject();
    if (Named(function, "Description") is string description) definition["description"] = description;
    var triggers = new JsonArray();
    var http = new JsonArray();
    var tables = new JsonObject();
    var uses = new JsonObject();
    var limits = new JsonObject();

    foreach (var a in type.GetAttributes())
    {
        var args = a.ConstructorArguments;
        switch (a.AttributeClass?.ToDisplayString())
        {
            case "Dcms.Functions.HttpAttribute":
                foreach (var who in Strings(args[0])) http.Add(Camel(who));
                break;
            case "Dcms.Functions.OnRowAttribute":
                var row = new JsonObject { ["kind"] = "row", ["table"] = (string?)args[0].Value, ["event"] = Event(args[1]) };
                if (Named(a, "Fields") is ImmutableArray<TypedConstant> fields) row["fields"] = new JsonArray([.. fields.Select(f => JsonValue.Create((string?)f.Value))]);
                triggers.Add(row);
                break;
            case "Dcms.Functions.OnRelationAttribute":
                triggers.Add(new JsonObject { ["kind"] = "relation", ["relationship"] = (string?)args[0].Value, ["event"] = Event(args[1]) });
                break;
            case "Dcms.Functions.OnAppPublishedAttribute":
                triggers.Add(new JsonObject { ["kind"] = "app", ["app"] = (string?)args[0].Value, ["event"] = "published" });
                break;
            case "Dcms.Functions.OnMessageAttribute":
                triggers.Add(new JsonObject { ["kind"] = "message", ["topic"] = (string?)args[0].Value });
                break;
            case "Dcms.Functions.OnScheduleAttribute":
                triggers.Add(new JsonObject { ["kind"] = "schedule", ["cron"] = (string?)args[0].Value });
                break;
            case "Dcms.Functions.OnEveryAttribute":
                triggers.Add(new JsonObject { ["kind"] = "every", ["interval"] = (string?)args[0].Value });
                break;
            case "Dcms.Functions.OnEventAttribute":
                triggers.Add(new JsonObject { ["kind"] = "event", ["name"] = (string?)args[0].Value });
                break;
            case "Dcms.Functions.UsesTableAttribute":
                var verbs = tables[(string?)args[0].Value ?? ""] as JsonArray ?? [];
                foreach (var verb in Strings(args[1])) verbs.Add(verb);
                tables[(string?)args[0].Value ?? ""] = verbs;
                break;
            case "Dcms.Functions.UsesContractsAttribute": Append(uses, "contracts", Strings(args[0])); break;
            case "Dcms.Functions.UsesEgressAttribute": Append(uses, "egress", Strings(args[0])); break;
            case "Dcms.Functions.UsesSecretsAttribute": Append(uses, "secrets", Strings(args[0])); break;
            case "Dcms.Functions.UsesNotificationsAttribute": Append(uses, "notifications", Strings(args[0])); break;
            case "Dcms.Functions.UsesLinksAttribute": Append(uses, "links", Strings(args[0])); break;
            case "Dcms.Functions.EmitsAttribute":
                var messages = uses["messages"] as JsonObject ?? [];
                Append(messages, "emit", Strings(args[0]));
                uses["messages"] = messages;
                break;
            case "Dcms.Functions.UsesEmailAttribute": uses["email"] = true; break;
            case "Dcms.Functions.UsesContentAttribute": uses["content"] = true; break;
            case "Dcms.Functions.LimitsAttribute":
                if (Named(a, "TimeoutMs") is int timeout and not 0) limits["timeoutMs"] = timeout;
                if (Named(a, "HeapMb") is int heap and not 0) limits["heapMb"] = heap;
                break;
        }
    }
    if (tables.Count > 0) uses["tables"] = tables;
    if (triggers.Count > 0) definition["triggers"] = triggers;
    if (http.Count > 0) definition["access"] = new JsonObject { ["http"] = http };
    if (uses.Count > 0) definition["uses"] = uses;
    if (limits.Count > 0) definition["limits"] = limits;
    return definition;
}

// Enum arguments come as their underlying values: their member names are what the definition says.
static string Event(TypedConstant value) =>
    value.Type is INamedTypeSymbol e && e.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => Equals(f.ConstantValue, value.Value)) is { } member
        ? member.Name.ToLowerInvariant()
        : "";

static IEnumerable<string> Strings(TypedConstant value) =>
    value.Kind == TypedConstantKind.Array
        ? value.Values.Select(v => v.Type?.TypeKind == TypeKind.Enum ? Name(v) : v.Value as string ?? "")
        : [];

static string Name(TypedConstant value) =>
    (value.Type as INamedTypeSymbol)?.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => Equals(f.ConstantValue, value.Value))?.Name ?? "";

static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

static object? Named(AttributeData attribute, string name) =>
    attribute.NamedArguments.FirstOrDefault(n => n.Key == name).Value is { IsNull: false } v
        ? v.Kind == TypedConstantKind.Array ? v.Values : v.Value
        : null;

static void Append(JsonObject target, string key, IEnumerable<string> values)
{
    var list = target[key] as JsonArray ?? [];
    foreach (var value in values) list.Add(value);
    target[key] = list;
}
