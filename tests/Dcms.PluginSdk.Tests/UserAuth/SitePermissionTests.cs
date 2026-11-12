using Dcms.Plugins.UserAuth.Api;

namespace Dcms.PluginSdk.Tests.UserAuth;

/// <summary>Site permission keys (ADR 0022): <c>{plugin}:{instance}:{resource}:{action}</c>.</summary>
public sealed class SitePermissionTests
{
    [Theory]
    [InlineData("dynamic-apps:crm:table:deals:read")]
    [InlineData("dynamic-apps:crm:flow:approve_deal:run")]
    [InlineData("forms:contact:submissions:read")]
    [InlineData("functions:hr:fn:approvals/validate:invoke")]
    public void Keys_name_a_plugin_an_instance_a_resource_and_an_action(string key) =>
        SitePermission.IsValid(key).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("dynamic-apps:crm:read")]
    [InlineData("Dynamic-Apps:crm:table:deals:read")]
    [InlineData("dynamic-apps:crm:table:deals:read ")]
    [InlineData("dynamic-apps::table:deals:read")]
    [InlineData("plugin:dynamic-apps:data-read")]
    [InlineData("functions/x:hr:fn:validate:invoke")]
    [InlineData("functions:hr:fn:/validate:invoke")]
    public void Anything_else_is_refused(string key) =>
        SitePermission.IsValid(key).Should().BeFalse();

    [Theory]
    [InlineData("dynamic-apps", "crm", "table:deals", "read")]
    [InlineData("functions", "hr", "fn:payroll/calculate", "invoke")] // what Functions offers for a function in a folder
    public void Of_builds_the_key_it_validates(string plugin, string instance, string resource, string action) =>
        SitePermission.IsValid(SitePermission.Of(plugin, instance, resource, action)).Should().BeTrue();
}
