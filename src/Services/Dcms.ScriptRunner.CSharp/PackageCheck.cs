using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Dcms.Functions.Compilation;

/// <summary>
/// Whether a NuGet package's assemblies can run in the sandbox (ADR 0031): every assembly they
/// reference is in the curated BCL, the SDK or the packages themselves, and none declares native
/// code. Read from metadata only: nothing in a package is loaded or run here.
/// </summary>
public static class PackageCheck
{
    public static List<string> Problems(IReadOnlyList<(string Package, string Name, byte[] Bytes)> packages, IReadOnlySet<string> curated)
    {
        var problems = new List<string>();
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readers = new List<(string Package, string Name, PEReader Pe)>();
        foreach (var (package, name, bytes) in packages)
        {
            var pe = new PEReader(new MemoryStream(bytes, writable: false));
            if (!pe.HasMetadata)
            {
                problems.Add($"{package}: {name} is native code, which a function cannot run.");
                pe.Dispose();
                continue;
            }
            own.Add(pe.GetMetadataReader().GetString(pe.GetMetadataReader().GetAssemblyDefinition().Name));
            readers.Add((package, name, pe));
        }
        foreach (var (package, name, pe) in readers)
        {
            using (pe)
            {
                var metadata = pe.GetMetadataReader();
                var missing = metadata.AssemblyReferences
                    .Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name))
                    .Where(r => !curated.Contains(r) && !own.Contains(r))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
                if (missing.Count > 0)
                {
                    problems.Add($"{package}: {name} needs {string.Join(", ", missing)}, which a function does not have.");
                }
                var native = metadata.MethodDefinitions
                    .Select(metadata.GetMethodDefinition)
                    .Any(m => (m.Attributes & MethodAttributes.PinvokeImpl) != 0);
                if (native)
                {
                    problems.Add($"{package}: {name} calls native code (DllImport), which a function cannot run.");
                }
            }
        }
        return problems;
    }
}
