using System.Text;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Cms;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Ai;

/// <summary>
/// Assembles the system prompt for AI generation in the Mode A builder.
///
/// A Mode A site is now plain HTML and CSS files in a git repo, so that is what
/// the model is asked for. Three things make the output usable rather than merely
/// valid: the component catalogue (whose identity classes turn generated markup
/// into real, editable components on the canvas), the theme variables (so the
/// result looks like the site rather than like a generic template), and the
/// tenant's enabled plugin instances (so a data-bound block references a slug
/// that actually exists).
///
/// The catalogue arrives from the caller because it is defined in TypeScript
/// (`@dcms/gjs-blocks`) and is per-tenant once generated plugin components are
/// included; the plugin instances come from the database, which is authoritative.
/// </summary>
public sealed class AiPromptBuilder(IPluginCatalog catalog, CmsDbContext db)
{
    /// <summary>Keeps a large catalogue from crowding out the actual instruction.</summary>
    private const int MaxComponents = 160;
    private const int MaxDocsLength = 120;

    private const string OutputContract = """
        You write the source of a static website: HTML fragments and CSS rules.

        Rules:
        - Output strictly valid JSON. No prose, no markdown code fences.
        - HTML is a page *body* fragment: no <html>, <head>, <body> or <script> tags.
        - Every element you style must carry a class; style it in the CSS you return,
          never with a style="" attribute.
        - Use the theme variables listed below (var(--dcms-…)) for colour, spacing,
          radius and fonts instead of hard-coded values.
        - Prefer flow layout (sections, flex, grid). Do not use absolute positioning.
        - Images must have alt text. Links opening a new tab must carry rel="noopener".
        - Write real, specific copy for the subject at hand, not lorem ipsum.
        """;

    private const string ComponentContract = """
        Build from the component catalogue below. A component is recognised by its
        identity class, so `<section class="dcms-hero">…</section>` becomes an
        editable Hero on the canvas. Markup that uses no identity class is still
        valid — it just stays a plain element.
        """;

    private const string PluginContract = """
        Data-bound content is emitted as an inert placeholder, which the published
        page fills in at run time:
          <div class="<identity class>"
               data-dcms-component="<component type>"
               data-dcms-props='{"heading":"Latest posts","layout":"cards"}'
               data-dcms-bindings='[{"propPath":"items","instanceSlug":"<slug>","query":{"contentType":"<type>","pageSize":6}}]'></div>
        Only use an instanceSlug and contentType from the list below; a placeholder
        naming anything else renders empty. Leave the element itself empty.
        """;

    public async Task<string> BuildSystemPromptAsync(
        IReadOnlyList<ComponentHint>? components,
        IReadOnlyList<string>? themeVariables,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine(OutputContract);
        sb.AppendLine();

        AppendThemeVariables(sb, themeVariables);
        AppendComponents(sb, components);
        await AppendPluginInstancesAsync(sb, ct);

        return sb.ToString();
    }

    private static void AppendThemeVariables(StringBuilder sb, IReadOnlyList<string>? variables)
    {
        if (variables is not { Count: > 0 })
        {
            return;
        }
        sb.AppendLine("Theme variables available in CSS:");
        sb.AppendLine("  " + string.Join(", ", variables.Distinct().Take(64)));
        sb.AppendLine();
    }

    private static void AppendComponents(StringBuilder sb, IReadOnlyList<ComponentHint>? components)
    {
        if (components is not { Count: > 0 })
        {
            return;
        }

        sb.AppendLine(ComponentContract);
        sb.AppendLine();

        foreach (var group in components.Take(MaxComponents).GroupBy(c => c.Category ?? "other"))
        {
            sb.AppendLine($"{group.Key}:");
            foreach (var component in group)
            {
                var identity = string.IsNullOrWhiteSpace(component.IdentityClass) ? "-" : component.IdentityClass;
                var children = component.AcceptsChildren ? ", holds children" : string.Empty;
                var docs = Truncate(component.Docs);
                sb.AppendLine($"  - {component.Label}: <{component.Tag} class=\"{identity}\">{children}{docs}");
            }
        }
        sb.AppendLine();
    }

    private async Task AppendPluginInstancesAsync(StringBuilder sb, CancellationToken ct)
    {
        var instances = await db.PluginInstances.AsNoTracking()
            .Where(p => p.Enabled)
            .Select(p => new { p.PluginId, p.Slug, p.Name })
            .ToListAsync(ct);

        if (instances.Count == 0)
        {
            sb.AppendLine("This site has no plugin instances enabled, so do not emit any data-bound placeholder.");
            return;
        }

        sb.AppendLine(PluginContract);
        sb.AppendLine();
        sb.AppendLine("Enabled plugin instances (instanceSlug — name — content types):");
        foreach (var instance in instances)
        {
            var manifest = catalog.Find(instance.PluginId);
            var types = manifest is null
                ? "none"
                : string.Join(", ", manifest.ContentTypes.Select(t => t.Name));
            sb.AppendLine($"  - {instance.Slug} — {instance.Name} — {types}");
        }
    }

    private static string Truncate(string? docs)
    {
        if (string.IsNullOrWhiteSpace(docs))
        {
            return string.Empty;
        }
        var trimmed = docs.Length > MaxDocsLength ? docs[..MaxDocsLength] + "…" : docs;
        return $" — {trimmed}";
    }
}

/// <summary>
/// One component of the builder's catalogue, as the admin describes it. Mirrors
/// the fields of `DcmsComponentSpec` the model needs to emit correct markup.
/// </summary>
public sealed record ComponentHint(
    string Type,
    string Label,
    string Tag,
    string? IdentityClass,
    string? Category,
    string? Docs,
    bool AcceptsChildren);
