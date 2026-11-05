using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json.Nodes;
using Dcms.Functions;

// dotnet-harness.js boots this, then calls Entry.Run once; Main only has to exist.
return;

/// <summary>
/// What dotnet-harness.js calls (ADR 0030). The tenant's assembly comes with the start message;
/// its host calls go back through the harness's <c>dcms.call</c>, the same JSON-lines relay a
/// TypeScript function's <c>__dcms.call</c> uses, so the broker cannot tell the languages apart.
/// Nothing here is a boundary: the Deno process around it has no permission to lose.
/// </summary>
public static partial class Entry
{
    private static (Assembly Assembly, byte[] Pdb)? s_loaded;

    [JSImport("call", "dcms")]
    private static partial Task<string> Call(string op, string args);

    [JSImport("log", "dcms")]
    private static partial void Log(string level, string message);

    /// <returns><c>{"ok":true,"result":…}</c>, or <c>{"ok":false,"error":"…","limit":bool}</c>.</returns>
    private static readonly Dictionary<string, Assembly> s_packages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A NuGet package's assembly (ADR 0031), loaded before the function's: found by name when
    /// the function, or another package, needs it.
    /// </summary>
    [JSExport]
    public static void Load(byte[] dll)
    {
        if (s_packages.Count == 0)
        {
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
                s_packages.TryGetValue(new AssemblyName(args.Name).Name ?? "", out var found) ? found : null;
        }
        var assembly = Assembly.Load(dll);
        s_packages[assembly.GetName().Name!] = assembly;
    }

    [JSExport]
    public static async Task<string> Run(byte[] dll, byte[] pdb, string type, string input, string context)
    {
        Assembly? tenant = null;
        try
        {
            // A served process (#607) gets the assembly with its first run only: the rest reuse it,
            // statics and all, which is the point of keeping it warm.
            if (dll.Length > 0)
            {
                s_loaded = (Assembly.Load(dll, pdb.Length == 0 ? null : pdb), pdb);
            }
            (tenant, pdb) = s_loaded ?? throw new InvalidOperationException("No assembly was loaded.");
            var runnable = Activator.CreateInstance(tenant.GetType(type, throwOnError: true)!) as IRunnable
                ?? throw new InvalidOperationException($"{type} is not a Function<TIn, TOut>.");
            var ctx = new Context(CallAsync, Log, JsonNode.Parse(context) as JsonObject);
            var result = await runnable.RunAsync(JsonNode.Parse(input), ctx);
            return new JsonObject { ["ok"] = true, ["result"] = result }.ToJsonString();
        }
        catch (Exception e)
        {
            var error = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = Describe(error, tenant, pdb),
                // Out of the sandbox's memory: a limit the run hit, not a bug to retry.
                ["limit"] = error is OutOfMemoryException or InsufficientExecutionStackException,
            }.ToJsonString();
        }
    }

    private static async Task<string> CallAsync(string op, string args)
    {
        try
        {
            return await Call(op, args);
        }
        catch (JSException e)
        {
            // The harness rejects with the host's own message: what the function did wrong.
            throw new HostException(e.Message.StartsWith("Error: ", StringComparison.Ordinal) ? e.Message[7..] : e.Message);
        }
    }

    /// <summary>
    /// The exception and the tenant's frames as <c>file:line</c>. Mono on WebAssembly prints no
    /// line numbers even with the PDB loaded, but it keeps each frame's IL offset, which the PDB
    /// maps to a line. Frames outside the tenant's code are left out: they would only show the SDK.
    /// </summary>
    internal static string Describe(Exception e, Assembly? tenant, byte[] pdb)
    {
        var text = new StringBuilder($"{e.GetType().Name}: {e.Message}");
        if (tenant is null || pdb.Length == 0) return text.ToString();
        try
        {
            using var provider = MetadataReaderProvider.FromPortablePdbImage(System.Collections.Immutable.ImmutableArray.Create(pdb));
            var reader = provider.GetMetadataReader();
            foreach (var frame in new System.Diagnostics.StackTrace(e).GetFrames())
            {
                if (frame.GetMethod() is not { } method || method.Module.Assembly != tenant) continue;
                var line = Line(reader, method.MetadataToken, frame.GetILOffset());
                text.Append($"\n   at {Name(method)}{(line is { } at ? $" in {at.File}:line {at.Line}" : "")}");
            }
        }
        catch (BadImageFormatException)
        {
            // Not a portable PDB: the message alone.
        }
        return text.ToString();
    }

    private static (string File, int Line)? Line(MetadataReader pdb, int token, int ilOffset)
    {
        if (ilOffset < 0) return null;
        var handle = (MethodDefinitionHandle)MetadataTokens.EntityHandle(token);
        (string, int)? best = null;
        foreach (var point in pdb.GetMethodDebugInformation(handle).GetSequencePoints())
        {
            if (point.IsHidden) continue;
            if (point.Offset > ilOffset) break;
            best = (pdb.GetString(pdb.GetDocument(point.Document).Name), point.StartLine);
        }
        return best;
    }

    /// <summary><c>OrderTotal.Run</c>, also for the state machine an async method compiles to (<c>&lt;Run&gt;d__4.MoveNext</c>).</summary>
    private static string Name(MethodBase method)
    {
        var type = method.DeclaringType;
        if (type is { Name: ['<', ..] name, DeclaringType: { } outer } && name.IndexOf('>') is > 1 and var end)
        {
            return $"{outer.Name}.{name[1..end]}";
        }
        return $"{type?.Name}.{method.Name}";
    }
}
