using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Dcms.Functions.Compilation;

/// <summary>
/// How tenant C# is compiled (ADR 0030), shared word for word by the build (dcms-csc) and the
/// IDE's language service (Dcms.Functions.LanguageWorker links this file): what the editor accepts
/// is what the build accepts.
///
/// <para>No project file, no MSBuild, no analyzers or source generators, no NuGet: only the
/// repository's <c>.cs</c> files, the generated SDK, and the curated BCL the sandbox ships. So
/// nothing of the tenant's runs at build time, and nothing it compiles against is missing at run time.</para>
/// </summary>
public static class FunctionCompilation
{
    /// <summary>Where the generated half of the SDK sits among the sources; never a repository path.</summary>
    public const string SdkPath = ".dcms/Sdk.g.cs";

    public const string UsingsPath = ".dcms/Usings.g.cs";

    /// <summary>The assembly the SDK's static half is; always referenced.</summary>
    public const string SdkAssembly = "Dcms.Functions";

    /// <summary>What a .NET project's implicit usings give, plus the SDK: a function file needs no using to start.</summary>
    public const string GlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using System.Threading.Tasks;
        global using System.Text.Json.Nodes;
        global using Dcms.Functions;
        """;

    public static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.CSharp14, DocumentationMode.Parse, SourceCodeKind.Regular);

    public static readonly CSharpCompilationOptions CompilationOptions = new(
        OutputKind.DynamicallyLinkedLibrary,
        nullableContextOptions: NullableContextOptions.Enable,
        optimizationLevel: OptimizationLevel.Release,
        allowUnsafe: false,
        deterministic: true,
        // One thread: the build runs under a memory cap beside the functions of other tenants.
        concurrentBuild: false);

    public static bool IsSource(string path) =>
        path.EndsWith(".cs", StringComparison.Ordinal) && !path.StartsWith(".dcms/", StringComparison.Ordinal);

    public static SyntaxTree Parse(string path, string text) =>
        CSharpSyntaxTree.ParseText(text, ParseOptions, path, System.Text.Encoding.UTF8);

    public static CSharpCompilation Create(IEnumerable<SyntaxTree> trees, IEnumerable<MetadataReference> references) =>
        CSharpCompilation.Create("functions", trees, references, CompilationOptions);

    /// <summary>The curated BCL's assembly names (Dcms.Functions.WasmHost's CuratedAssemblies.txt), plus the SDK's.</summary>
    public static IReadOnlySet<string> Curated(string list) =>
        list.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Append(SdkAssembly)
            .ToHashSet(StringComparer.Ordinal);
}
