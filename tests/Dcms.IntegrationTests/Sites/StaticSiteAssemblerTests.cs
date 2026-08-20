extern alias SiteBuilderApp;
using System.Text;
using Dcms.Shared.Data.Sites;
using StaticSiteAssembler = SiteBuilderApp::Dcms.SiteBuilder.StaticSiteAssembler;

namespace Dcms.IntegrationTests.Sites;

/// <summary>Pure assembler test — no infrastructure, runs everywhere.</summary>
public class StaticSiteAssemblerTests
{
    private const string Manifest = """
        {
          "version": 2,
          "theme": { "colors": { "brand": "#0f172a" } },
          "nav": [{ "label": "Home", "path": "/" }, { "label": "About", "path": "/about" }],
          "pages": [
            {
              "id": "home", "slug": "home", "path": "/", "title": "Home", "home": true,
              "seo": { "title": "Welcome to Acme", "description": "Acme home", "ogImage": "/hero.png" }
            },
            {
              "id": "about", "slug": "about", "path": "/about", "title": "About",
              "seo": { "title": "About Acme" }
            }
          ],
          "settings": { "lang": "cs", "favicon": "/favicon.ico", "headHtml": "<meta name=\"x\" content=\"1\" />" }
        }
        """;

    private static Dictionary<string, string> Files(params (string Path, string Content)[] extra)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["site.json"] = Manifest,
            ["pages/home.html"] = "<section class=\"hero\"><h1>Acme</h1></section>",
            ["pages/about.html"] = "<section><h1>About</h1></section>",
            ["styles/theme.css"] = ":root{--dcms-color-brand:#0f172a;}",
            ["styles/global.css"] = "body{margin:0;}",
            ["styles/pages/home.css"] = ".hero{padding:4rem;}",
        };
        foreach (var (path, content) in extra)
        {
            files[path] = content;
        }
        return files;
    }

    [Fact]
    public void Embeds_a_consent_policy_defaulting_to_asking_first()
    {
        var home = new StaticSiteAssembler().Assemble(Files()).Pages
            .Single(p => p.FileName == "index.html").Html;

        home.Should().Contain("id=\"dcms-consent\"");
        // Nothing in the manifest says anything about consent, so the site must
        // still default to asking rather than to tracking silently.
        home.Should().Contain("\"mode\":\"banner\"");
        home.Should().Contain("\"analytics\":true");
    }

    [Fact]
    public void Marks_the_consent_policy_when_the_tenant_records_no_analytics()
    {
        var home = new StaticSiteAssembler().Assemble(Files(), analyticsEnabled: false).Pages
            .Single(p => p.FileName == "index.html").Html;

        // A site that stores nothing must not greet visitors with a cookie banner.
        home.Should().Contain("\"analytics\":false");
    }

    [Fact]
    public void Wraps_each_page_body_in_a_document_shell()
    {
        var site = new StaticSiteAssembler().Assemble(Files());

        var home = site.Pages.Single(p => p.FileName == "index.html").Html;
        home.Should().Contain("<title>Welcome to Acme</title>");
        home.Should().Contain("<meta name=\"description\" content=\"Acme home\" />");
        home.Should().Contain("<meta property=\"og:image\" content=\"/hero.png\" />");
        home.Should().Contain("<link rel=\"icon\" href=\"/favicon.ico\" />");
        home.Should().Contain("<html lang=\"cs\">");
        home.Should().Contain("<meta name=\"x\" content=\"1\" />");

        // The authored markup is emitted verbatim — that is the point of the format.
        home.Should().Contain("<section class=\"hero\"><h1>Acme</h1></section>");

        site.Pages.Should().Contain(p => p.FileName == "about.html");
        site.Pages.Single(p => p.FileName == "about.html").Html.Should().Contain("<title>About Acme</title>");
    }

    [Fact]
    public void Links_stylesheets_in_cascade_order()
    {
        var html = new StaticSiteAssembler().Assemble(Files()).Pages
            .Single(p => p.FileName == "index.html").Html;

        var theme = html.IndexOf("/styles/theme.css", StringComparison.Ordinal);
        var global = html.IndexOf("/styles/global.css", StringComparison.Ordinal);
        var page = html.IndexOf("/styles/pages/home.css", StringComparison.Ordinal);

        theme.Should().BeGreaterThan(-1);
        global.Should().BeGreaterThan(theme);
        page.Should().BeGreaterThan(global);
    }

    [Fact]
    public void Emits_the_hydration_script_on_every_page()
    {
        var site = new StaticSiteAssembler().Assemble(Files());
        site.Pages.Should().OnlyContain(p => p.Html.Contains("/_dcms/hydrate.js"));
    }

    [Fact]
    public void Emits_navigation_when_enabled_and_escapes_it()
    {
        var files = Files();
        files["site.json"] = Manifest.Replace(
            "{ \"label\": \"About\", \"path\": \"/about\" }",
            "{ \"label\": \"A&B <script>\", \"path\": \"/about\" }");

        var html = new StaticSiteAssembler().Assemble(files).Pages
            .Single(p => p.FileName == "index.html").Html;

        html.Should().Contain("<nav class=\"dcms-nav\">");
        html.Should().Contain("A&amp;B &lt;script&gt;");
        html.Should().NotContain("<script>A");
    }

    [Fact]
    public void Omits_navigation_when_the_manifest_disables_it()
    {
        var files = Files();
        files["site.json"] = Manifest.Replace("\"lang\": \"cs\"", "\"lang\": \"cs\", \"renderNav\": false");

        var html = new StaticSiteAssembler().Assemble(files).Pages
            .Single(p => p.FileName == "index.html").Html;

        html.Should().NotContain("dcms-nav");
    }

    [Fact]
    public void Copies_stylesheets_and_assets_but_not_the_source()
    {
        var site = new StaticSiteAssembler().Assemble(Files(
            ("assets.json", "[]"),
            ("robots.txt", "User-agent: *")));

        var names = site.Files.Select(f => f.FileName).ToList();
        names.Should().Contain("styles/global.css");
        names.Should().Contain("styles/theme.css");
        names.Should().Contain("robots.txt");

        // Source files are consumed, not published.
        names.Should().NotContain("site.json");
        names.Should().NotContain("assets.json");
        names.Should().NotContain(n => n.StartsWith("pages/", StringComparison.Ordinal));

        site.Files.Single(f => f.FileName == "styles/global.css").ContentType.Should().Be("text/css");
    }

    [Fact]
    public void Decodes_base64_binary_assets()
    {
        var png = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]);
        var site = new StaticSiteAssembler().Assemble(Files(("img/logo.png", png)));

        var file = site.Files.Single(f => f.FileName == "img/logo.png");
        file.Content.Should().Equal([0x89, 0x50, 0x4E, 0x47]);
        file.ContentType.Should().Be("image/png");
    }

    [Fact]
    public void Fails_with_an_actionable_message_when_a_binary_asset_is_not_base64()
    {
        var act = () => new StaticSiteAssembler().Assemble(Files(("img/logo.png", "not base64!!")));
        act.Should().Throw<InvalidOperationException>().WithMessage("*img/logo.png*base64*");
    }

    [Fact]
    public void Publishes_a_home_page_at_index_html_even_when_its_route_is_not_root()
    {
        var files = Files();
        files["site.json"] = Manifest.Replace("\"path\": \"/\", \"title\": \"Home\"", "\"path\": \"/start\", \"title\": \"Home\"");

        var site = new StaticSiteAssembler().Assemble(files);

        site.Pages.Should().Contain(p => p.FileName == "start.html");
        site.Pages.Should().Contain(p => p.FileName == "index.html");
        site.Pages.Single(p => p.FileName == "index.html").Html
            .Should().Contain("<title>Welcome to Acme</title>");
    }

    [Fact]
    public void Renders_a_page_whose_html_file_is_missing_as_an_empty_body()
    {
        var files = Files();
        files.Remove("pages/about.html");

        var html = new StaticSiteAssembler().Assemble(files).Pages
            .Single(p => p.FileName == "about.html").Html;

        html.Should().Contain("<title>About Acme</title>");
        html.Should().Contain("<body>");
    }

    [Fact]
    public void Rejects_a_missing_or_legacy_manifest_instead_of_publishing_an_empty_site()
    {
        var noManifest = new Dictionary<string, string> { ["pages/home.html"] = "<p>hi</p>" };
        var actMissing = () => new StaticSiteAssembler().Assemble(noManifest);
        actMissing.Should().Throw<InvalidOperationException>().WithMessage("*site.json*");

        var legacy = new Dictionary<string, string>
        {
            ["site.json"] = """{"version":1,"theme":{},"pages":[],"nav":[]}""",
        };
        var actLegacy = () => new StaticSiteAssembler().Assemble(legacy);
        actLegacy.Should().Throw<InvalidOperationException>().WithMessage("*version 2*");
    }

    [Fact]
    public void Rejects_a_manifest_with_no_pages()
    {
        var files = new Dictionary<string, string> { ["site.json"] = """{"version":2,"pages":[]}""" };
        var act = () => new StaticSiteAssembler().Assemble(files);
        act.Should().Throw<InvalidOperationException>().WithMessage("*no pages*");
    }

    [Theory]
    [InlineData("/", "index.html")]
    [InlineData("", "index.html")]
    [InlineData("/about", "about.html")]
    [InlineData("/about/team", "about_team.html")]
    public void Maps_a_route_to_the_published_file_name(string path, string expected)
        => StaticSiteAssembler.FileNameFor(path).Should().Be(expected);

    [Fact]
    public void Marks_a_noindex_page()
    {
        var files = Files();
        files["site.json"] = Manifest.Replace("\"title\": \"About Acme\"", "\"title\": \"About Acme\", \"noIndex\": true");

        var html = new StaticSiteAssembler().Assemble(files).Pages
            .Single(p => p.FileName == "about.html").Html;

        html.Should().Contain("<meta name=\"robots\" content=\"noindex\" />");
    }

    [Fact]
    public void Parses_a_manifest_with_only_the_required_fields()
    {
        var manifest = SiteManifest.TryParse("""
            {"version":2,"pages":[{"id":"p","slug":"home","path":"/","title":"T","seo":{"title":"T"}}]}
            """);

        manifest.Should().NotBeNull();
        manifest!.Settings.Lang.Should().Be("en");
        manifest.Settings.RenderNav.Should().BeTrue();
        manifest.HomePage()!.Slug.Should().Be("home");
    }

    [Fact]
    public void Round_trips_a_file_map_through_the_shared_helper()
    {
        var files = Files();
        var parsed = SiteFileMap.Parse(SiteFileMap.Serialize(files));
        parsed.Should().BeEquivalentTo(files);
        SiteFileMap.Hash("x").Should().Be(SiteFileMap.Hash("x"));
        SiteFileMap.Hash("x").Should().NotBe(SiteFileMap.Hash("y"));
    }

    [Fact]
    public void Treats_only_the_documented_extensions_as_binary()
    {
        SiteFileMap.IsBinaryPath("img/logo.png").Should().BeTrue();
        SiteFileMap.IsBinaryPath("fonts/i.woff2").Should().BeTrue();
        // SVG is text so it stays diffable in git.
        SiteFileMap.IsBinaryPath("img/logo.svg").Should().BeFalse();
        SiteFileMap.IsBinaryPath("styles/global.css").Should().BeFalse();
    }

    [Fact]
    public void Considers_both_authoring_modes_git_backed()
    {
        SiteRenderMode.StaticPrerender.IsGitBacked().Should().BeTrue();
        SiteRenderMode.ReactApp.IsGitBacked().Should().BeTrue();
        SiteRenderMode.StaticFiles.IsGitBacked().Should().BeFalse();
    }

    private const string ChromeManifest = """
        {
          "version": 2,
          "pages": [
            { "id": "home", "slug": "home", "path": "/", "title": "Home", "home": true, "seo": { "title": "Home" } },
            { "id": "landing", "slug": "landing", "path": "/landing", "title": "Landing", "layout": "", "seo": { "title": "Landing" } }
          ],
          "regions": [
            { "id": "r-head", "slug": "header", "label": "Header", "placement": "before" },
            { "id": "r-foot", "slug": "footer", "label": "Footer", "placement": "after" }
          ],
          "layouts": [{ "id": "default", "label": "Default", "regions": ["r-head", "r-foot"], "default": true }],
          "settings": { "lang": "en", "renderNav": false }
        }
        """;

    private static Dictionary<string, string> ChromeFiles() => new(StringComparer.Ordinal)
    {
        ["site.json"] = ChromeManifest,
        ["pages/home.html"] = "<main>Home body</main>",
        ["pages/landing.html"] = "<main>Landing body</main>",
        ["regions/header.html"] = "<header>Site header</header>",
        ["regions/footer.html"] = "<footer>Site footer</footer>",
        ["styles/global.css"] = "body{margin:0;}",
    };

    [Fact]
    public void Wraps_a_page_in_the_regions_its_layout_names()
    {
        var html = new StaticSiteAssembler().Assemble(ChromeFiles()).Pages
            .Single(p => p.FileName == "index.html").Html;

        html.Should().Contain("Site header");
        html.Should().Contain("Site footer");
        // Order is what makes a header a header: before the page, after it a footer.
        html.IndexOf("Site header", StringComparison.Ordinal)
            .Should().BeLessThan(html.IndexOf("Home body", StringComparison.Ordinal));
        html.IndexOf("Home body", StringComparison.Ordinal)
            .Should().BeLessThan(html.IndexOf("Site footer", StringComparison.Ordinal));
    }

    [Fact]
    public void Lets_a_page_opt_out_of_the_shared_chrome()
    {
        // An empty layout is a real choice — a landing page that must not show the
        // site navigation — and is not the same as leaving it unset.
        var html = new StaticSiteAssembler().Assemble(ChromeFiles()).Pages
            .Single(p => p.FileName == "landing.html").Html;

        html.Should().Contain("Landing body");
        html.Should().NotContain("Site header");
        html.Should().NotContain("Site footer");
    }

    [Fact]
    public void Never_publishes_region_or_component_source()
    {
        var files = ChromeFiles();
        files["blocks/promo.json"] = """{ "version": 1, "name": "promo", "label": "Promo", "template": "<div>x</div>" }""";
        var site = new StaticSiteAssembler().Assemble(files);

        // Serving these would expose the source and put markup fragments at
        // guessable URLs; they are consumed into the document instead.
        var names = site.Files.Select(f => f.FileName).ToList();
        names.Should().NotContain("regions/header.html");
        names.Should().NotContain("blocks/promo.json");
        names.Should().Contain("styles/global.css");
    }

    [Fact]
    public void Embeds_the_component_registry_and_the_route_table()
    {
        var files = ChromeFiles();
        files["blocks/promo.json"] = """{ "version": 1, "name": "promo", "label": "Promo", "template": "<div>x</div>" }""";
        var html = new StaticSiteAssembler().Assemble(files).Pages
            .Single(p => p.FileName == "index.html").Html;

        html.Should().Contain("id=\"dcms-components\"");
        html.Should().Contain("id=\"dcms-routes\"");
        // Every page's title, so a breadcrumb in a shared region can name the
        // levels above the one being viewed.
        html.Should().Contain("Landing");
    }

    [Fact]
    public void Escapes_a_closing_script_tag_in_embedded_json()
    {
        var files = ChromeFiles();
        files["blocks/promo.json"] =
            """{ "version": 1, "name": "promo", "label": "Promo", "template": "<div></script></div>" }""";
        var html = new StaticSiteAssembler().Assemble(files).Pages
            .Single(p => p.FileName == "index.html").Html;

        // A template that could close its own script element would let authored
        // content escape into the document as markup.
        html.Should().NotContain("<div></script>");
        html.Should().Contain("\u003c");
    }

    [Fact]
    public void Skips_a_component_definition_that_is_not_valid_json()
    {
        var files = ChromeFiles();
        files["blocks/broken.json"] = "{ not json";
        files["blocks/promo.json"] = """{ "version": 1, "name": "promo", "label": "Promo", "template": "<div>x</div>" }""";

        var html = new StaticSiteAssembler().Assemble(files).Pages
            .Single(p => p.FileName == "index.html").Html;

        // One half-typed file in the code view must not fail the whole publish.
        html.Should().Contain("promo");
        html.Should().NotContain("broken");
    }

    [Fact]
    public void Produces_utf8_page_content()
    {
        var files = Files();
        files["pages/home.html"] = "<p>Příliš žluťoučký kůň</p>";
        var html = new StaticSiteAssembler().Assemble(files).Pages
            .Single(p => p.FileName == "index.html").Html;

        html.Should().Contain("Příliš žluťoučký kůň");
        Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(html)).Should().Be(html);
    }

    [Fact]
    public void Publishes_a_detail_route_under_a_wildcard_file_name()
    {
        var files = Files(("pages/event.html", "<article>Event</article>"));
        files["site.json"] = WithDetailPage(Manifest);

        var site = new StaticSiteAssembler().Assemble(files);

        // `@` cannot occur in a real segment (they are kebab-case), so the site
        // host can tell "the page for any event" from "the page for /events/at".
        site.Pages.Should().Contain(p => p.FileName == "events_@.html");
    }

    [Fact]
    public void Leaves_detail_routes_out_of_the_navigation_route_table()
    {
        var files = Files(("pages/event.html", "<article>Event</article>"));
        files["site.json"] = WithDetailPage(Manifest);

        var home = new StaticSiteAssembler().Assemble(files).Pages
            .Single(p => p.FileName == "index.html").Html;

        // A detail route is not an address: it can never match the page being
        // viewed, and a menu built from it would link to a literal colon.
        home.Should().NotContain("/events/:slug");
        home.Should().Contain("/about");
    }

    /// <summary>The shared manifest with an `/events/:slug` detail page added.</summary>
    private static string WithDetailPage(string manifest) => manifest.Replace(
        """
            {
              "id": "about", "slug": "about", "path": "/about", "title": "About",
        """,
        """
            {
              "id": "event", "slug": "event", "path": "/events/:slug", "title": "Event",
              "seo": { "title": "Event" }
            },
            {
              "id": "about", "slug": "about", "path": "/about", "title": "About",
        """,
        StringComparison.Ordinal);

    [Theory]
    [InlineData("/events/:slug", "events_@.html")]
    [InlineData("/about/team", "about_team.html")]
    [InlineData("/", "index.html")]
    public void Maps_a_route_to_its_published_file_name(string path, string expected)
    {
        StaticSiteAssembler.FileNameFor(path).Should().Be(expected);
    }
}
