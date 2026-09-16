using System.Text;

namespace Dcms.AdminApi.ApiClientGen;

/// <summary>
/// The starter templates a new Mode B site can begin from, embedded from
/// <c>packages/site-template-react</c>.
///
/// <para>A materialised site is three layers, later winning: <c>shared/</c> (index.html, Vite and
/// TypeScript config, the API client instance), the template's own files (its pages, styles,
/// <c>package.json</c> and <c>AGENTS.md</c>), and the <see cref="GeneratedLayer"/> for this tenant.
/// Every template is a complete project the site builder can install and build, and every one
/// carries the generated client, so there is no longer a starter choice that produces a site which
/// cannot build or cannot reach its own content.</para>
///
/// <para><c>shared/src/api/</c> in the package is a <i>fixture</i>: real emitter output for a
/// sample tenant, committed so the package's typecheck proves the templates compile against what the
/// generator really writes. It is never shipped — the tenant's own layer replaces it — which is why
/// it is not embedded.</para>
/// </summary>
public static class SiteTemplates
{
    private const string Prefix = "Dcms.AdminApi.SiteTemplates/";

    /// <summary>In the order the editor offers them.</summary>
    public static readonly IReadOnlyList<string> Ids = ["blank", "content", "landing"];

    public static bool Exists(string? id) => id is not null && Ids.Contains(id, StringComparer.Ordinal);

    /// <summary>A whole new site: shared files, then the template, then the tenant's generated layer.</summary>
    public static IReadOnlyDictionary<string, string> Materialize(string id, GeneratedFiles generated)
    {
        if (!Exists(id))
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown site template.");
        }

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, content) in Read("shared/"))
        {
            files[path] = content;
        }
        foreach (var (path, content) in Read($"templates/{id}/"))
        {
            files[path] = content;
        }
        foreach (var (path, content) in generated.Files)
        {
            files[path] = content;
        }
        return files;
    }

    /// <summary>
    /// A downloadable copy for local development: the content template, plus a <c>.env</c> that
    /// points at the tenant's domain.
    ///
    /// <para>Only the download gets one. A site built by DCMS is served from that same domain and
    /// reaches its API same-origin; writing the absolute URL into its repository would make the
    /// published bundle call itself cross-origin, and break the day the domain changes.</para>
    /// </summary>
    public static byte[] BuildDownload(TenantApiSnapshot api, GeneratedFiles generated)
    {
        var files = new Dictionary<string, string>(Materialize("content", generated), StringComparer.Ordinal)
        {
            [".env"] = new StringBuilder()
                .Append("VITE_API_BASE_URL=").Append(api.Servers.FirstOrDefault() ?? string.Empty).Append('\n')
                .ToString(),
        };
        return PackageIo.Zip(files);
    }

    /// <summary>The analytics and consent runtime every site ships under <c>src/dcms/</c>.</summary>
    internal static IEnumerable<(string Path, string Content)> DcmsRuntime() =>
        Read("shared/").Where(f => f.Path.StartsWith("src/dcms/", StringComparison.Ordinal));

    private static IEnumerable<(string Path, string Content)> Read(string folder)
    {
        var prefix = Prefix + folder;
        foreach (var name in PackageIo.EmbeddedNames(prefix).Order(StringComparer.Ordinal))
        {
            yield return (name[prefix.Length..].Replace('\\', '/'), PackageIo.ReadEmbedded(name));
        }
    }
}
