using Dcms.PluginSdk.Runtime;
using Dcms.Plugins.Articles;
using Dcms.Plugins.Blog;

namespace Dcms.PluginSdk.Tests;

public class PluginRouteTableTests
{
    [Fact]
    public void Records_content_list_and_get_routes_for_articles_and_blog()
    {
        var registry = new PluginRegistry([new ArticlesPlugin(), new BlogPlugin()]);
        var table = new PluginRouteTable(registry);

        var article = table.Find("articles", "article");
        article.Should().NotBeNull();
        article!.List.Should().BeTrue();
        article.GetBySlug.Should().BeTrue();

        var post = table.Find("blog", "post");
        post.Should().NotBeNull();
        post!.List.Should().BeTrue();
        post.GetBySlug.Should().BeTrue();

        table.Find("articles", "nonexistent").Should().BeNull();
    }

    [Fact]
    public void Article_and_post_content_types_are_searchable_with_title_slug()
    {
        var article = new ArticlesPlugin().Manifest.ContentTypes.Single(t => t.Name == "article");
        article.Searchable.Should().BeTrue();
        article.SlugField.Should().Be("title");
        article.Fields.Select(f => f.Name).Should().Contain(["title", "body", "heroImage", "related"]);

        var post = new BlogPlugin().Manifest.ContentTypes.Single(t => t.Name == "post");
        post.SlugField.Should().Be("title");
    }
}
