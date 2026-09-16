using System.Security.Cryptography;
using System.Text;

namespace Dcms.Shared.Data.Cms;

/// <summary>
/// A short hash of everything about a tenant's plugin instances that shapes its content-API
/// OpenAPI document.
///
/// <para>One function, because two services cache that document and a third generates a site's
/// typed client from it. They used to carry separate copies of this hash, and a field added to
/// one copy and not the other is a cache that serves a stale document — or a site whose client
/// is reported current when it is not.</para>
///
/// <para>Ordered by id, so the same set of instances hashes the same whatever order a query
/// returned them in.</para>
/// </summary>
public static class PluginInstanceFingerprint
{
    public static string Of(IEnumerable<PluginInstance> instances)
    {
        var sb = new StringBuilder();
        foreach (var p in instances.OrderBy(i => i.Id))
        {
            sb.Append(p.Id).Append('|').Append(p.Slug).Append('|').Append(p.PluginId)
              .Append('|').Append(p.PluginVersion).Append('|').Append(p.Name)
              .Append('|').Append(p.Description).Append('|').Append(p.ConfigJson).Append(';');
        }
        return Hash(sb.ToString());
    }

    /// <summary>The same 16-hex-character digest, over any string. For callers extending the key.</summary>
    public static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
