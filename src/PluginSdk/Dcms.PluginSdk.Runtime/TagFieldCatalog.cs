using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Cms;

namespace Dcms.PluginSdk.Runtime;

/// <summary>
/// Which content fields hold tags, as <see cref="TagQueries"/> needs it: every plugin field
/// declared <see cref="ContentFieldType.Tags"/>, and every content type that carries a
/// tenant's own fields (whose tag-typed ones are found in each instance's config).
/// </summary>
public static class TagFieldCatalog
{
    public static TagQueries.TagFields TagFields(this IPluginCatalog catalog) => new(
        [
            .. from m in catalog.Manifests
               from t in m.ContentTypes
               from f in t.Fields
               where f.Type == ContentFieldType.Tags
               select new TagQueries.DeclaredTagField(m.Id, t.Name, f.Name),
        ],
        [
            .. from m in catalog.Manifests
               from t in m.ContentTypes
               where t.CustomFields is not null
               select new TagQueries.CustomFieldSource(m.Id, t.Name, t.CustomFields!.ValuesField, t.CustomFields.ConfigKey),
        ]);
}
