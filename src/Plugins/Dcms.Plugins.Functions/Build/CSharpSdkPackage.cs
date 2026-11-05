using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Dcms.Plugins.Functions.Build;

/// <summary>
/// Dcms.Functions.Sdk (ADR 0031): what a desktop C# build of a functions repository references,
/// made from what this server runs, so it never drifts from it. Dcms.Functions itself, the usings
/// and C# version dcms-csc builds with, and a target that cuts the framework down to the curated
/// BCL. `dcms sdk` saves it in the clone's own package folder: no registry, no sign-in to one.
///
/// <para>The version is the content's hash, so a changed SDK is a new version and never a stale
/// copy in a NuGet cache.</para>
/// </summary>
public static class CSharpSdkPackage
{
    public const string Id = "Dcms.Functions.Sdk";

    private static readonly Lazy<(byte[] Bytes, string Version)> Built = new(Build);

    public static byte[] Bytes => Built.Value.Bytes;

    public static string Version => Built.Value.Version;

    public static string FileName => $"{Id.ToLowerInvariant()}.{Version}.nupkg";

    /// <summary>The curated BCL's assembly names: packages of these names are the framework's, never installed.</summary>
    public static readonly IReadOnlySet<string> Framework = Encoding.UTF8.GetString(Resource("CuratedAssemblies.txt"))
        .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static (byte[], string) Build()
    {
        var dll = typeof(Dcms.Functions.Function<,>).Assembly.Location;
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["lib/net10.0/Dcms.Functions.dll"] = File.ReadAllBytes(dll),
            ["build/Dcms.Functions.Sdk.props"] = Resource("Dcms.Functions.Sdk.props"),
            ["build/Dcms.Functions.Sdk.targets"] = Resource("Dcms.Functions.Sdk.targets"),
            ["build/CuratedAssemblies.txt"] = Resource("CuratedAssemblies.txt"),
        };
        // The docs are what an editor shows on hover; a server without them still builds.
        if (File.Exists(Path.ChangeExtension(dll, ".xml")))
        {
            files["lib/net10.0/Dcms.Functions.xml"] = File.ReadAllBytes(Path.ChangeExtension(dll, ".xml"));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, bytes) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            hash.AppendData(bytes);
        }
        var version = $"1.0.0-sdk.{Convert.ToHexStringLower(hash.GetHashAndReset())[..12]}";

        files[$"{Id}.nuspec"] = Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{Id}</id>
                <version>{version}</version>
                <authors>DCMS</authors>
                <description>Build DCMS C# functions on your own machine as the server builds them.</description>
                <dependencies><group targetFramework="net10.0" /></dependencies>
              </metadata>
            </package>
            """);
        files["[Content_Types].xml"] = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="utf-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="nuspec" ContentType="application/octet" />
              <Default Extension="dll" ContentType="application/octet" />
              <Default Extension="xml" ContentType="application/octet" />
              <Default Extension="props" ContentType="application/octet" />
              <Default Extension="targets" ContentType="application/octet" />
              <Default Extension="txt" ContentType="application/octet" />
            </Types>
            """);

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in files)
            {
                using var entry = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
                entry.Write(bytes);
            }
        }
        return (buffer.ToArray(), version);
    }

    private static byte[] Resource(string name)
    {
        using var stream = typeof(CSharpSdkPackage).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not embedded.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
