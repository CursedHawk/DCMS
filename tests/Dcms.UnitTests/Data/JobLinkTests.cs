using Dcms.Shared.Data.Jobs;

namespace Dcms.UnitTests.Data;

/// <summary>A job's links are rendered as links in the admin console, so only its own paths pass.</summary>
public class JobLinkTests
{
    [Theory]
    [InlineData("/plugins/crm", true)]
    [InlineData("/plugins/crm/configuration?tab=automation&run=1", true)]
    [InlineData("//evil.example/x", false)]
    [InlineData("https://evil.example", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("", false)]
    [InlineData("/\t/evil.example", false)]
    [InlineData("/\n/evil.example", false)]
    [InlineData("/\r/evil.example", false)]
    [InlineData("/ /evil.example", false)]
    [InlineData("\t//evil.example", false)]
    [InlineData("/plugins/crm\n", false)]
    [InlineData("/%2F/evil.example", true)]
    public void Only_an_admin_path_is_a_link(string path, bool ok) =>
        JobLinkRels.IsAdminPath(path).Should().Be(ok);
}
