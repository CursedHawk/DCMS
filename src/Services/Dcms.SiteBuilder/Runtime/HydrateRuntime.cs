using System.Reflection;

namespace Dcms.SiteBuilder.Runtime;

/// <summary>
/// The Mode A client hydration script, embedded at build time and loaded once.
/// Uploaded to every Mode A build at /_dcms/hydrate.js (see SitePublishConsumer);
/// the prerendered pages reference it to populate data-bound placeholders.
/// </summary>
public static class HydrateRuntime
{
    public static byte[] Bytes { get; } = Load("Dcms.SiteBuilder.Runtime.hydrate.js");

    /// <summary>An embedded runtime file of this assembly.</summary>
    internal static byte[] Load(string name)
    {
        var assembly = typeof(HydrateRuntime).Assembly;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded runtime '{name}' not found. Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
