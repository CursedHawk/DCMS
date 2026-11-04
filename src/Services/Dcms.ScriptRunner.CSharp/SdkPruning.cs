using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Dcms.Functions.Compilation;

/// <summary>
/// The generated SDK cut down to what the repository declares (ADR 0030). Sdk.g.cs describes
/// every table and contract of the environment, megabytes of it in a large workspace; compiled
/// whole into the tenant's assembly, every run would carry it. A function reaches only the tables
/// in its [UsesTable] and the contracts in its [UsesContracts] (the broker refuses anything else),
/// so the build keeps those and drops the rest: using an undeclared one is a compile error that
/// says to declare it, rather than a refusal at run time. The editor keeps the whole SDK, for
/// discovering what there is.
/// </summary>
public static class SdkPruning
{
    private static readonly HashSet<string> Roots = new(StringComparer.Ordinal) { "WorkspaceSdk", "Database", "Plugins" };

    public static string Prune(string sdk, IEnumerable<SyntaxTree> repository)
    {
        if (sdk.Length == 0) return sdk;
        var (tables, contracts) = Declared(repository);
        var root = CSharpSyntaxTree.ParseText(sdk, FunctionCompilation.ParseOptions).GetRoot();

        // Table properties of undeclared tables, contract classes of undeclared contracts.
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var drop = new List<SyntaxNode>();
        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            if (Call(property, "Table") is [var app, var table, ..] && !tables.Contains($"{app}.{table}")) drop.Add(property);
        }
        foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var contract = type.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(i => Literals(i, "Contract")).FirstOrDefault(l => l.Count >= 2);
            if (contract is not null && !contracts.Contains(contract[1]))
            {
                drop.Add(type);
                removed.Add(type.Identifier.Text);
            }
        }
        root = root.RemoveNodes(drop, SyntaxRemoveOptions.KeepNoTrivia)!;

        // Row records no kept table uses; then, until nothing changes, members typed by a removed
        // class and classes left empty.
        var rows = root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .SelectMany(p => p.DescendantNodes().OfType<GenericNameSyntax>())
            .Where(g => g.Identifier.Text == "Table")
            .SelectMany(g => g.TypeArgumentList.Arguments.Select(a => a.ToString()))
            .ToHashSet(StringComparer.Ordinal);
        var unused = root.DescendantNodes().OfType<RecordDeclarationSyntax>().Where(r => !rows.Contains(r.Identifier.Text)).ToList();
        root = root.RemoveNodes(unused, SyntaxRemoveOptions.KeepNoTrivia)!;
        while (true)
        {
            var typed = root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Where(p => removed.Contains(p.Type.ToString())).Cast<SyntaxNode>();
            var empty = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(c => !Roots.Contains(c.Identifier.Text) && c.Members.Count == 0).ToList();
            var next = typed.Concat(empty).ToList();
            if (next.Count == 0) break;
            foreach (var c in empty) removed.Add(c.Identifier.Text);
            root = root.RemoveNodes(next, SyntaxRemoveOptions.KeepNoTrivia)!;
        }
        return root.ToFullString();
    }

    /// <summary>A missing member of the generated SDK most likely means an undeclared table or contract: say so.</summary>
    public static string Hint(Diagnostic diagnostic)
    {
        if (diagnostic.Id != "CS1061") return "";
        var message = diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        if (message.StartsWith("'Database'", StringComparison.Ordinal) || message.Contains("App' does not contain", StringComparison.Ordinal))
            return " A table is in ctx.Db once the function declares it: [UsesTable(\"app.table\", \"read\")].";
        if (message.StartsWith("'Plugins'", StringComparison.Ordinal) || message.Contains("Plugin' does not contain", StringComparison.Ordinal))
            return " A contract is in ctx.Plugins once the function declares it: [UsesContracts(\"name@1\")].";
        return "";
    }

    private static (HashSet<string> Tables, HashSet<string> Contracts) Declared(IEnumerable<SyntaxTree> repository)
    {
        var (tables, contracts) = (new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        foreach (var attribute in repository.SelectMany(t => t.GetRoot().DescendantNodes().OfType<AttributeSyntax>()))
        {
            var name = attribute.Name.ToString().Split('.').Last();
            var args = attribute.ArgumentList?.Arguments.Select(a => a.Expression).OfType<LiteralExpressionSyntax>()
                .Select(l => l.Token.ValueText).ToList() ?? [];
            if (name is "UsesTable" or "UsesTableAttribute" && args.Count > 0) tables.Add(args[0]);
            if (name is "UsesContracts" or "UsesContractsAttribute") contracts.UnionWith(args.Select(c => c.Split('@')[0]));
        }
        return (tables, contracts);
    }

    private static List<string>? Call(PropertyDeclarationSyntax property, string method) =>
        property.ExpressionBody?.Expression is InvocationExpressionSyntax invocation ? Literals(invocation, method) : null;

    /// <summary>The string literals of <c>ctx.{method}&lt;…&gt;("a", "b", …)</c>; empty for any other call.</summary>
    private static List<string> Literals(InvocationExpressionSyntax invocation, string method) =>
        invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.Text: var name } } && name == method
            ? invocation.ArgumentList.Arguments.Select(a => a.Expression).OfType<LiteralExpressionSyntax>().Select(l => l.Token.ValueText).ToList()
            : [];
}
