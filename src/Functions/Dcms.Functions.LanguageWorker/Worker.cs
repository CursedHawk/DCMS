using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Functions.Compilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;

// csharp.worker.ts boots this, then calls Worker's exports; Main only has to exist.
return;

/// <summary>
/// The IDE's C# language service (ADR 0030): one Roslyn workspace holding a repository's
/// <c>.cs</c> files, the generated SDK and the usings, compiled exactly as dcms-csc compiles them.
/// Positions are character offsets into a file's text; answers are JSON.
/// </summary>
public static partial class Worker
{
    private static readonly AdhocWorkspace Workspace = new(MefHostServices.Create(MefHostServices.DefaultAssemblies));
    private static readonly Dictionary<string, DocumentId> Documents = new(StringComparer.Ordinal);
    private static ProjectId? _project;
    private static (DocumentId Document, int Offset, CompletionList List)? _lastCompletion;

    private static Project Project
    {
        get
        {
            if (_project is { } id) return Workspace.CurrentSolution.GetProject(id)!;
            var info = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "functions", "functions", LanguageNames.CSharp,
                compilationOptions: FunctionCompilation.CompilationOptions,
                parseOptions: FunctionCompilation.ParseOptions,
                metadataReferences: References());
            _project = Workspace.AddProject(info).Id;
            Set(FunctionCompilation.UsingsPath, FunctionCompilation.GlobalUsings);
            return Workspace.CurrentSolution.GetProject(_project)!;
        }
    }

    private static List<MetadataReference>? s_framework;

    /// <summary>
    /// The repository's NuGet packages' assemblies (ADR 0031), base64, as the build compiles
    /// against them: replaces whatever set was there, the curated BCL and the SDK staying.
    /// </summary>
    [JSExport]
    public static void Packages(string json)
    {
        s_framework ??= References();
        using var list = System.Text.Json.JsonDocument.Parse(json);
        var packages = list.RootElement.EnumerateArray()
            .Select(p => (MetadataReference)MetadataReference.CreateFromImage(Convert.FromBase64String(p.GetString() ?? "")));
        Workspace.TryApplyChanges(Project.WithMetadataReferences(s_framework.Concat(packages)).Solution);
    }

    private static List<MetadataReference> References()
    {
        var assembly = typeof(Worker).Assembly;
        var references = new List<MetadataReference>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("refs/", StringComparison.Ordinal) && n.EndsWith(".dll", StringComparison.Ordinal)))
        {
            var docs = assembly.GetManifestResourceStream(name[..^4] + ".xml") is { } xml ? XmlDocumentationProvider.CreateFromBytes(Read(xml)) : null;
            references.Add(MetadataReference.CreateFromImage(Read(assembly.GetManifestResourceStream(name)!), documentation: docs));
        }
        return references;
    }

    private static byte[] Read(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>Adds or replaces a file. The generated SDK goes in as <see cref="FunctionCompilation.SdkPath"/>.</summary>
    [JSExport]
    public static void Set(string path, string text)
    {
        var solution = Project.Solution;
        if (Documents.TryGetValue(path, out var id))
        {
            solution = solution.WithDocumentText(id, SourceText.From(text, Encoding.UTF8));
        }
        else
        {
            id = DocumentId.CreateNewId(_project!);
            Documents[path] = id;
            solution = solution.AddDocument(id, path, SourceText.From(text, Encoding.UTF8), filePath: path);
        }
        Workspace.TryApplyChanges(solution);
    }

    [JSExport]
    public static void Remove(string path)
    {
        if (Documents.Remove(path, out var id))
        {
            Workspace.TryApplyChanges(Workspace.CurrentSolution.RemoveDocument(id));
        }
    }

    private static Document? Document(string path) =>
        Documents.TryGetValue(path, out var id) ? Workspace.CurrentSolution.GetDocument(id) : null;

    /// <summary>Errors and warnings in one file: <c>[{message, severity, code, start, end}]</c>.</summary>
    [JSExport]
    public static async Task<string> Diagnostics(string path)
    {
        if (Document(path) is not { } document) return "[]";
        var model = await document.GetSemanticModelAsync();
        return new JsonArray([.. model!.GetDiagnostics()
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(d => (JsonNode)new JsonObject
            {
                ["message"] = d.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
                ["severity"] = d.Severity == DiagnosticSeverity.Error ? "error" : "warning",
                ["code"] = d.Id,
                ["start"] = d.Location.SourceSpan.Start,
                ["end"] = d.Location.SourceSpan.End,
            })]).ToJsonString();
    }

    /// <summary><c>{start, end, items:[{label, insert, filter, sort, kind}]}</c>: what may go at <paramref name="offset"/>, replacing start..end.</summary>
    [JSExport]
    public static async Task<string> Complete(string path, int offset)
    {
        if (Document(path) is not { } document || CompletionService.GetService(document) is not { } service) return "null";
        var list = await service.GetCompletionsAsync(document, offset);
        _lastCompletion = (document.Id, offset, list);
        return new JsonObject
        {
            ["start"] = list.Span.Start,
            ["end"] = list.Span.End,
            ["items"] = new JsonArray([.. list.ItemsList.Take(1_000).Select((item, index) => (JsonNode)new JsonObject
            {
                ["index"] = index,
                ["label"] = item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix,
                ["insert"] = item.Properties.TryGetValue("InsertionText", out var insertion) ? insertion : item.DisplayText,
                ["filter"] = item.FilterText,
                ["sort"] = item.SortText,
                ["kind"] = item.Tags.FirstOrDefault() ?? "",
                ["detail"] = item.InlineDescription,
            })]),
        }.ToJsonString();
    }

    /// <summary>The description of item <paramref name="index"/> of the last completion list, as markdown.</summary>
    [JSExport]
    public static async Task<string> Resolve(int index)
    {
        if (_lastCompletion is not { } last || Workspace.CurrentSolution.GetDocument(last.Document) is not { } document
            || index < 0 || index >= last.List.ItemsList.Count || CompletionService.GetService(document) is not { } service)
        {
            return "";
        }
        var description = await service.GetDescriptionAsync(document, last.List.ItemsList[index]);
        return description is null ? "" : Markdown(description.TaggedParts);
    }

    /// <summary>What hovering at <paramref name="offset"/> shows, as markdown: <c>{text, start, end}</c> or null.</summary>
    [JSExport]
    public static async Task<string> Hover(string path, int offset)
    {
        if (Document(path) is not { } document || QuickInfoService.GetService(document) is not { } service) return "null";
        var info = await service.GetQuickInfoAsync(document, offset);
        if (info is null || info.Sections.IsEmpty) return "null";
        var text = string.Join("\n\n", info.Sections.Select(s => s.Kind == QuickInfoSectionKinds.Description
            ? "```csharp\n" + s.Text + "\n```"
            : Markdown(s.TaggedParts)));
        return new JsonObject { ["text"] = text, ["start"] = info.Span.Start, ["end"] = info.Span.End }.ToJsonString();
    }

    /// <summary>
    /// The overloads of the call <paramref name="offset"/> is in the arguments of:
    /// <c>{active, parameter, signatures:[{label, documentation, parameters:[label]}]}</c>, or null.
    /// </summary>
    [JSExport]
    public static async Task<string> Signature(string path, int offset)
    {
        if (Document(path) is not { } document) return "null";
        var root = (await document.GetSyntaxRootAsync())!;
        var model = (await document.GetSemanticModelAsync())!;
        var token = root.FindToken(Math.Max(0, offset - 1));
        var arguments = token.Parent?.AncestorsAndSelf().OfType<BaseArgumentListSyntax>()
            .FirstOrDefault(a => a.OpenToken().Span.End <= offset && (a.CloseToken().IsMissing || offset <= a.CloseToken().SpanStart));
        if (arguments?.Parent is not { } call) return "null";

        var methods = model.GetMemberGroup(call is InvocationExpressionSyntax invocation ? invocation.Expression : call)
            .OfType<IMethodSymbol>().ToList();
        if (methods.Count == 0 && model.GetSymbolInfo(call).Symbol is IMethodSymbol constructor) methods.Add(constructor);
        if (methods.Count == 0 && model.GetTypeInfo(call).Type is INamedTypeSymbol created) methods.AddRange(created.InstanceConstructors);
        if (methods.Count == 0) return "null";

        var parameter = arguments.Arguments.GetSeparators().Count(s => s.SpanStart < offset);
        var chosen = model.GetSymbolInfo(call).Symbol as IMethodSymbol;
        var format = SymbolDisplayFormat.MinimallyQualifiedFormat;
        return new JsonObject
        {
            ["active"] = Math.Max(0, chosen is null ? methods.FindIndex(m => m.Parameters.Length > parameter) : methods.IndexOf(chosen)),
            ["parameter"] = parameter,
            ["signatures"] = new JsonArray([.. methods.Select(m => (JsonNode)new JsonObject
            {
                ["label"] = m.ToMinimalDisplayString(model, call.SpanStart, format),
                ["documentation"] = Summary(m),
                ["parameters"] = new JsonArray([.. m.Parameters.Select(p => (JsonNode)p.ToMinimalDisplayString(model, call.SpanStart, format))]),
            })]),
        }.ToJsonString();
    }

    /// <summary>Where the symbol at <paramref name="offset"/> is declared in source: <c>[{path, start, end}]</c>.</summary>
    [JSExport]
    public static async Task<string> Definition(string path, int offset)
    {
        if (Document(path) is not { } document) return "[]";
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset);
        return Locations(symbol is null ? [] : symbol.Locations.Where(l => l.IsInSource));
    }

    /// <summary>Every reference to the symbol at <paramref name="offset"/>, and its declarations: <c>[{path, start, end}]</c>.</summary>
    [JSExport]
    public static async Task<string> References(string path, int offset)
    {
        if (Document(path) is not { } document) return "[]";
        var symbol = (await SymbolFinder.FindSymbolAtPositionAsync(document, offset))?.OriginalDefinition;
        if (symbol is null) return "[]";
        // Not SymbolFinder.FindReferencesAsync: its index goes through Roslyn's persistent storage,
        // whose setup asks for the current process, which WebAssembly has none of. A repository is
        // small enough to look at every identifier of the name.
        var found = new List<Location>(symbol.Locations.Where(l => l.IsInSource));
        foreach (var other in Project.Documents)
        {
            var root = await other.GetSyntaxRootAsync();
            var model = await other.GetSemanticModelAsync();
            foreach (var name in root!.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == symbol.Name))
            {
                if (name.Parent is { } node && model!.GetSymbolInfo(node).Symbol is { } target
                    && SymbolEqualityComparer.Default.Equals(target.OriginalDefinition, symbol))
                {
                    found.Add(name.GetLocation());
                }
            }
        }
        return Locations(found);
    }

    /// <summary>The file formatted the way C# is: its whole new text.</summary>
    [JSExport]
    public static async Task<string> Format(string path)
    {
        if (Document(path) is not { } document) return "";
        return (await (await Formatter.FormatAsync(document)).GetTextAsync()).ToString();
    }

    /// <summary>
    /// What each name in <paramref name="start"/>..<paramref name="end"/> is, for colouring:
    /// <c>[start, length, type, modifiers]*</c> in semanticTokens.ts's legend. Keywords, strings
    /// and comments are left to Monaco's grammar.
    /// </summary>
    [JSExport]
    public static async Task<string> Classify(string path, int start, int end)
    {
        if (Document(path) is not { } document) return "[]";
        var length = (await document.GetTextAsync()).Length;
        start = Math.Clamp(start, 0, length);
        var spans = await Classifier.GetClassifiedSpansAsync(document, TextSpan.FromBounds(start, Math.Clamp(end, start, length)));
        var tokens = new List<int>();
        foreach (var group in spans.GroupBy(s => s.TextSpan).OrderBy(g => g.Key.Start))
        {
            var type = -1;
            var modifiers = 0;
            foreach (var span in group)
            {
                if (span.ClassificationType == ClassificationTypeNames.ConstantName) modifiers = 1;
                if (TokenTypes.TryGetValue(span.ClassificationType, out var t)) type = t;
            }
            if (type >= 0) tokens.AddRange([group.Key.Start, group.Key.Length, type, modifiers]);
        }
        return JsonSerializer.Serialize(tokens);
    }

    /// <summary>Roslyn's classifications as indices into semanticTokens.ts's <c>LEGEND.tokenTypes</c>.</summary>
    private static readonly Dictionary<string, int> TokenTypes = new()
    {
        [ClassificationTypeNames.NamespaceName] = 0,
        [ClassificationTypeNames.ClassName] = 1,
        [ClassificationTypeNames.RecordClassName] = 1,
        [ClassificationTypeNames.EnumName] = 2,
        [ClassificationTypeNames.InterfaceName] = 3,
        [ClassificationTypeNames.StructName] = 4,
        [ClassificationTypeNames.RecordStructName] = 4,
        [ClassificationTypeNames.TypeParameterName] = 5,
        [ClassificationTypeNames.DelegateName] = 6,
        [ClassificationTypeNames.ParameterName] = 7,
        [ClassificationTypeNames.LocalName] = 8,
        [ClassificationTypeNames.ConstantName] = 8,
        [ClassificationTypeNames.PropertyName] = 9,
        [ClassificationTypeNames.FieldName] = 9,
        [ClassificationTypeNames.EnumMemberName] = 10,
        [ClassificationTypeNames.EventName] = 11,
        [ClassificationTypeNames.MethodName] = 13,
        [ClassificationTypeNames.ExtensionMethodName] = 13,
        [ClassificationTypeNames.LabelName] = 14,
    };

    private static string Locations(IEnumerable<Location> locations) =>
        new JsonArray([.. locations.Distinct().Select(l => (JsonNode)new JsonObject
        {
            ["path"] = l.SourceTree?.FilePath,
            ["start"] = l.SourceSpan.Start,
            ["end"] = l.SourceSpan.End,
        })]).ToJsonString();

    private static string Markdown(ImmutableArray<TaggedText> parts) =>
        string.Concat(parts.Select(p => p.Tag == TextTags.LineBreak ? "\n\n" : p.Text));

    private static string Summary(ISymbol symbol) =>
        symbol.GetDocumentationCommentXml() is { Length: > 0 } xml
        && System.Text.RegularExpressions.Regex.Match(xml, "<summary>(.*?)</summary>", System.Text.RegularExpressions.RegexOptions.Singleline) is { Success: true } m
            ? System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, @"<see cref=""\w:([^""]+)""\s*/>", "`$1`").Trim()
            : "";
}

internal static class ArgumentLists
{
    public static SyntaxToken OpenToken(this BaseArgumentListSyntax list) => list switch
    {
        ArgumentListSyntax a => a.OpenParenToken,
        BracketedArgumentListSyntax b => b.OpenBracketToken,
        _ => default,
    };

    public static SyntaxToken CloseToken(this BaseArgumentListSyntax list) => list switch
    {
        ArgumentListSyntax a => a.CloseParenToken,
        BracketedArgumentListSyntax b => b.CloseBracketToken,
        _ => default,
    };
}
