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
    [InlineData("moderate", "plugin:guestbook:book:moderate")]
    [InlineData("plugin:guestbook:moderate", "plugin:guestbook:book:moderate")]
    [InlineData("plugin:guestbook:other:moderate", "plugin:guestbook:other:moderate")]
    [InlineData("content:read", "content:read")]
    [InlineData("plugin:forms:read", "plugin:forms:read")]
    public void On_an_instance_the_plugins_own_keys_take_its_slug(string reference, string key) =>
        PluginPermissions.Resolve("guestbook", "book", reference).Should().Be(key);

    [Fact]
    public void Keys_are_held_per_instance_and_a_plugin_wide_one_on_any()
    {
        PluginPermissions.WithoutInstance("plugin:guestbook:book:moderate").Should().Be("plugin:guestbook:moderate");
        PluginPermissions.WithoutInstance("content:read").Should().Be("content:read");
        PluginPermissions.TryParse("repo:1:read", out _, out _, out _).Should().BeFalse();

        var held = new HashSet<string> { "plugin:guestbook:book:moderate" };
        new Dcms.Shared.Security.Authorization.PermissionRequirement("plugin:guestbook:moderate").IsMetBy(held).Should().BeFalse();
        new Dcms.Shared.Security.Authorization.PermissionRequirement("plugin:guestbook:moderate", anyInstance: true).IsMetBy(held).Should().BeTrue();
        new Dcms.Shared.Security.Authorization.PermissionRequirement("plugin:guestbook:read", anyInstance: true).IsMetBy(held).Should().BeFalse();
        new Dcms.Shared.Security.Authorization.PermissionRequirement("plugin:other:moderate", anyInstance: true).IsMetBy(held).Should().BeFalse();
    }

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
