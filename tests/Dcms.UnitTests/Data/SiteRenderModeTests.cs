using Dcms.Shared.Data.Sites;

namespace Dcms.UnitTests.Data;

/// <summary>
/// Which modes share the git source stack and which publish through the sandboxed Vite build.
/// Mode D (ReactBuilder) is both — a new mode that silently fell through to the Mode A
/// prerenderer would publish an empty site with a green build.
/// </summary>
public class SiteRenderModeTests
{
    [Theory]
    [InlineData(SiteRenderMode.StaticPrerender, true, false)]
    [InlineData(SiteRenderMode.ReactApp, true, true)]
    [InlineData(SiteRenderMode.StaticFiles, false, false)]
    [InlineData(SiteRenderMode.ReactBuilder, true, true)]
    public void Classifies_each_mode(SiteRenderMode mode, bool gitBacked, bool viteApp)
    {
        mode.IsGitBacked().Should().Be(gitBacked);
        mode.IsViteApp().Should().Be(viteApp);
    }

    [Fact]
    public void Every_mode_is_classified()
    {
        // A mode added to the enum without a row above is a mode nobody decided the publish
        // path for. Fail here rather than in production.
        Enum.GetValues<SiteRenderMode>().Should().HaveCount(4);
    }
}
