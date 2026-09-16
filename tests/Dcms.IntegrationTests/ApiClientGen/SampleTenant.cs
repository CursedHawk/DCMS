extern alias AdminApiApp;

using System.Text.Json;
using System.Text.Json.Nodes;
using AdminApiApp::Dcms.AdminApi.ApiClientGen;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;
using Dcms.Plugins.Analytics;
using Dcms.Plugins.Blog;
using Dcms.Plugins.Branding;
using Dcms.Plugins.Forms;
using Dcms.Plugins.LiveChat;
using Dcms.Plugins.Search;
using Dcms.Plugins.VisitorAuth;

namespace Dcms.IntegrationTests.ApiClientGen;

/// <summary>
/// A tenant's content-API document built by the real <see cref="OpenApiAssembler"/> from real
/// plugins — never a hand-written imitation of one.
///
/// <para>The generator's tests used to run against a fixture with <c>"get": {}</c> operations and
/// no responses. It resembled what content plugins emit closely enough to pass, and nothing like
/// what Forms or Branding emit, which is how a client that dropped form submissions and 404'd on
/// branding shipped with green tests.</para>
/// </summary>
internal static class SampleTenant
{
    public const string FormsConfig = """
        {
          "forms": [
            {
              "name": "contact",
              "title": "Contact us",
              "fields": [
                { "name": "name", "label": "Your name", "type": "text", "required": true, "maxLength": 200 },
                { "name": "email", "label": "Email", "type": "email", "required": true },
                { "name": "message", "label": "Message", "type": "textarea", "maxLength": 4000 },
                { "name": "newsletter", "label": "Send me the newsletter", "type": "checkbox" }
              ]
            }
          ]
        }
        """;

    private static readonly (string PluginId, string Slug, string Name, string Description, string Config)[] Instances =
    [
        ("blog", "news", "News", "Company announcements and product updates.", "{}"),
        ("branding", "brand", "Brand", "Public branding for the site header.", "{}"),
        ("forms", "enquiries", "Enquiries", "Visitor enquiries from the website.", FormsConfig),
        ("search", "find", "Search", "Site search.", "{}"),
        ("visitor-auth", "members", "Members", "Member accounts.", "{}"),
        ("live-chat", "support", "Support", "Support chat.", "{}"),
        ("analytics", "stats", "Stats", "Anonymous analytics.", "{}"),
    ];

    public static TenantApiSnapshot Snapshot(bool tagging = true, Func<string, string>? rename = null)
    {
        var registry = new PluginRegistry(
        [
            new BlogPlugin(), new BrandingPlugin(), new FormsPlugin(), new SearchPlugin(),
            new VisitorAuthPlugin(), new LiveChatPlugin(), new AnalyticsPlugin(),
        ]);
        var tenantId = Guid.Parse("00000000-0000-0000-0000-00000000a0a0");
        var instances = Instances
            .Select(i => (i.PluginId, Slug: rename?.Invoke(i.Slug) ?? i.Slug, i.Name, i.Description, i.Config))
            .ToList();
        var contexts = instances
            .Select((i, n) => new PluginInstanceContext(
                Guid.Parse($"00000000-0000-0000-0000-{n + 1:D12}"), tenantId, i.PluginId, i.Slug, i.Name, i.Description,
                JsonDocument.Parse(i.Config)))
            .ToList();

        JsonObject document = new OpenApiAssembler(registry).Build("acme", contexts, ["https://acme.example"], tagging);
        return new TenantApiSnapshot(
            document,
            instances.Select(i => new GeneratedInstance(i.Slug, i.PluginId, i.Name, i.Description)).ToList(),
            ["https://acme.example"],
            "acme",
            "sample");
    }
}
