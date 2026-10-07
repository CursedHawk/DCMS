using Dcms.Plugins.UserAuth.Api;

namespace Dcms.PluginSdk.Tests.UserAuth;

/// <summary>Site permission keys (ADR 0022): <c>{plugin}:{instance}:{resource}:{action}</c>.</summary>
public sealed class SitePermissionTests
{
    [Theory]
    [InlineData("dynamic-apps:crm:table:deals:read")]
    [InlineData("dynamic-apps:crm:flow:approve_deal:run")]
    [InlineData("forms:contact:submissions:read")]
    public void Keys_name_a_plugin_an_instance_a_resource_and_an_action(string key) =>
        SitePermission.IsValid(key).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("dynamic-apps:crm:read")]
    [InlineData("Dynamic-Apps:crm:table:deals:read")]
    [InlineData("dynamic-apps:crm:table:deals:read ")]
    [InlineData("dynamic-apps::table:deals:read")]
    [InlineData("plugin:dynamic-apps:data-read")]
    public void Anything_else_is_refused(string key) =>
        SitePermission.IsValid(key).Should().BeFalse();

    [Fact]
    public void Of_builds_the_key_it_validates() =>
        SitePermission.IsValid(SitePermission.Of("dynamic-apps", "crm", "table:deals", "read")).Should().BeTrue();
}
