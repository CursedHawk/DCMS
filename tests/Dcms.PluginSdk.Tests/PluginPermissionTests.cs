using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;

namespace Dcms.PluginSdk.Tests;

/// <summary>Permissions a plugin adds (ADR 0019): how references resolve and what the registry refuses.</summary>
public class PluginPermissionTests
{
    private sealed class Plugin(params PermissionDefinition[] permissions) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create("guestbook", "Guestbook", "", false, permissions: permissions);
    }

    [Theory]
    [InlineData("moderate", "plugin:guestbook:moderate")]
    [InlineData("content:read", "content:read")]
    [InlineData("plugin:forms:read", "plugin:forms:read")]
    public void A_bare_action_is_the_plugins_own_and_a_key_is_itself(string reference, string key) =>
        PluginPermissions.Resolve("guestbook", reference).Should().Be(key);

    [Theory]
    [InlineData("Moderate")]
    [InlineData("")]
    [InlineData("a:b")]
    public void Actions_are_kebab_case(string action)
    {
        var act = () => new PluginRegistry([new Plugin(new PermissionDefinition(action, "x"))]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*kebab-case*");
    }

    [Fact]
    public void Actions_are_unique()
    {
        var act = () => new PluginRegistry([new Plugin(new PermissionDefinition("read", "a"), new PermissionDefinition("read", "b"))]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*unique*");
    }

    [Fact]
    public void A_reference_is_declared_when_the_plugin_or_platform_has_it()
    {
        var manifest = new Plugin(new PermissionDefinition("moderate", "Moderate", "Approve entries", GrantToMembers: true)).Manifest;
        PluginRegistry.DeclaresPermission(manifest, "moderate").Should().BeTrue();
        PluginRegistry.DeclaresPermission(manifest, "plugin:guestbook:moderate").Should().BeTrue();
        PluginRegistry.DeclaresPermission(manifest, "content:read").Should().BeTrue();
        PluginRegistry.DeclaresPermission(manifest, "delete").Should().BeFalse();
        PluginRegistry.DeclaresPermission(manifest, "plugin:forms:read").Should().BeFalse("another plugin's key is not this plugin's to require");
    }
}
