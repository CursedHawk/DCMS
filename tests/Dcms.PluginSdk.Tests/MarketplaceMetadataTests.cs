using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;
using Dcms.Plugins.All;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Tests;

/// <summary>
/// The marketplace shows every shipped plugin as a card. A card with no category falls into
/// "Other" and a card with no summary falls back to the full description — both degrade rather
/// than break, which is right for a field that is optional by design.
///
/// <para>These tests exist anyway, because "degrades gracefully" and "nobody filled it in" look
/// identical from the shopfront, and the second is the one worth catching before release.</para>
/// </summary>
public class MarketplaceMetadataTests
{
    /// <summary>
    /// The real shipped set, through the same registration path the services use — so a plugin
    /// added to <c>DcmsPluginSet.AddAll</c> is covered here without anyone remembering to.
    /// </summary>
    private static IReadOnlyList<PluginManifest> Manifests() =>
        new ServiceCollection()
            .AddDcmsPlugins(p => p.AddAll())
            .BuildServiceProvider()
            .GetRequiredService<IPluginCatalog>()
            .Manifests
            .ToList();

    public static TheoryData<string> PluginIds()
    {
        var data = new TheoryData<string>();
        foreach (var m in Manifests()) data.Add(m.Id);
        return data;
    }

    private static PluginManifest Find(string id) => Manifests().Single(m => m.Id == id);

    [Theory]
    [MemberData(nameof(PluginIds))]
    public void Every_plugin_declares_a_category(string id)
    {
        Find(id).Category.Should().NotBeNullOrWhiteSpace(
            "an uncategorised plugin lands in \"Other\", where nobody browsing by purpose finds it");
    }

    [Theory]
    [MemberData(nameof(PluginIds))]
    public void Every_plugin_declares_a_one_line_summary(string id)
    {
        var manifest = Find(id);
        manifest.Summary.Should().NotBeNullOrWhiteSpace();
        // A summary that runs to a paragraph is a description, and the card has a description.
        manifest.Summary!.Length.Should().BeLessThan(120,
            "the summary is one line on a card; the paragraph goes in Description");
    }

    [Theory]
    [MemberData(nameof(PluginIds))]
    public void Every_plugin_declares_an_icon(string id)
    {
        var icon = Find(id).IconName;
        icon.Should().NotBeNullOrWhiteSpace();
        // Lucide names are PascalCase; the SPA looks them up verbatim and falls back to a
        // generic glyph on a miss, so a lowercase name is a silently generic card.
        icon.Should().MatchRegex("^[A-Z][A-Za-z0-9]*$");
    }

    [Fact]
    public void Categories_come_from_a_small_shared_set()
    {
        // Free text, but a marketplace with fourteen categories of one plugin each is a list
        // with extra steps. Keeping them few is what makes browsing by category worth doing.
        string[] known = ["Content", "Media", "Presentation", "Engagement", "Insight", "Integrations"];
        foreach (var manifest in Manifests())
        {
            known.Should().Contain(manifest.Category!,
                $"'{manifest.Id}' uses a category no other plugin does");
        }
    }
}
