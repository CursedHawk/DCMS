extern alias AdminApiApp;
using AdminApiApp::Dcms.AdminApi.Tenancy;

namespace Dcms.IntegrationTests.Sites;

public sealed class RepoRemoteTests
{
    [Theory]
    [InlineData("https://git.example.test/tenant-acme/functions-1.git", "tenant-acme/functions-1")]
    [InlineData("https://user:pw@git.example.test/tenant-acme/functions-1", "tenant-acme/functions-1")]
    [InlineData("ssh://git@git.example.test:2222/tenant-acme/functions-1.git", "tenant-acme/functions-1")]
    [InlineData("git@git.example.test:tenant-acme/functions-1.git", "tenant-acme/functions-1")]
    [InlineData("functions-1", null)]
    [InlineData("", null)]
    public void A_remote_names_its_repository(string remote, string? name)
        => MyAccountEndpoints.RepoNameOf(remote).Should().Be(name);
}
