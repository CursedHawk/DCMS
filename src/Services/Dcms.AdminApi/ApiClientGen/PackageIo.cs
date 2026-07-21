using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace Dcms.AdminApi.ApiClientGen;

/// <summary>Embedded-resource reading and zip packing shared by the client + starter builders.</summary>
internal static class PackageIo
{
    private static readonly Assembly Self = typeof(PackageIo).Assembly;

    public static string ReadEmbedded(string logicalName)
    {
        using var stream = Self.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded resource missing: {logicalName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IEnumerable<string> EmbeddedNames(string prefix)
        => Self.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal));

    public static byte[] Zip(IReadOnlyDictionary<string, string> files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(content);
            }
        }
        return buffer.ToArray();
    }
}
