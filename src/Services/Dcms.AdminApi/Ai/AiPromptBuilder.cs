using System.Text;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Cms;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Ai;

/// <summary>
/// Assembles the system prompt for AI generation: the component-tree contract,
/// the available layout/content components, and the tenant's enabled plugin
/// instances (so bound components can reference real instance slugs).
/// </summary>
public sealed class AiPromptBuilder(IPluginCatalog catalog, CmsDbContext db)
{
    private const string TreeContract = """
        Output strictly valid JSON. A ComponentNode is:
          { "id": string, "type": string, "props": object, "bindings": [], "children": [ComponentNode] }
        A SiteDefinition is:
          { "version": 1, "theme": { "colors": {}, "fonts": {} }, "nav": [{ "label", "path" }],
            "pages": [{ "id", "path", "title", "seo": { "title" }, "root": ComponentNode }] }
        Layout/content component types: Section, Stack, Grid, Hero(props: title, subtitle),
          Heading(props: text, variant), Text(props: text), Image(props: src, alt), Button(props: label, href).
        Plugin components bind to data via a binding:
          { "propPath": string, "source": { "instanceSlug": string, "query": {} } }.
        """;

    public async Task<string> BuildSystemPromptAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a website builder that emits a component tree as JSON. Respond with JSON only — no prose, no code fences.");
        sb.AppendLine();
        sb.AppendLine(TreeContract);
        sb.AppendLine();

        var instances = await db.PluginInstances.AsNoTracking()
            .Where(p => p.Enabled)
            .Select(p => new { p.PluginId, p.Slug, p.Name, p.Description })
            .ToListAsync(ct);

        if (instances.Count > 0)
        {
            sb.AppendLine("Enabled plugin instances available for data bindings (instanceSlug → plugin, content types):");
            foreach (var instance in instances)
            {
                var manifest = catalog.Find(instance.PluginId);
                var types = manifest is null ? string.Empty : string.Join(", ", manifest.ContentTypes.Select(t => t.Name));
                sb.AppendLine($"  - {instance.Slug} ({instance.PluginId}): {instance.Name} — content types: {types}");
            }
        }
        else
        {
            sb.AppendLine("No plugin instances are enabled; use only layout/content components.");
        }

        return sb.ToString();
    }
}
