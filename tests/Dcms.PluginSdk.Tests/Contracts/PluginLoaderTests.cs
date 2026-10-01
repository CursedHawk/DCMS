using System.Text.Json;
using Dcms.Plugins.All;
using Dcms.Plugins.Forms.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.PluginSdk.Runtime.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Tests.Contracts;

/// <summary>An operator-installed plugin, loaded from a folder, integrating with the built-in ones.</summary>
public class PluginLoaderTests
{
    private static readonly string Installed = Path.Combine(AppContext.BaseDirectory, "installed-plugins");

    [Fact]
    public void Loads_the_installed_plugin_with_the_hosts_contract_types()
    {
        var loaded = PluginLoader.LoadFrom(Installed);

        var guestbook = loaded.Should().ContainSingle().Which;
        guestbook.Manifest.Id.Should().Be("sample-guestbook");
        // Its own assembly, from its own load context...
        guestbook.GetType().Assembly.Location.Should().Contain("installed-plugins");
        // ...but the Forms hook it intercepts is the host's type, not a second copy of it.
        guestbook.Manifest.Intercepts!.Select(i => i.HookType).Should().Contain(typeof(FormSubmitting));
    }

    [Fact]
    public async Task The_installed_plugin_joins_the_ecosystem()
    {
        var installed = PluginLoader.LoadFrom(Installed);
        var registry = new ServiceCollection()
            .AddDcmsPlugins(p =>
            {
                p.AddAll();
                foreach (var plugin in installed)
                {
                    p.Add(plugin);
                }
            })
            .BuildServiceProvider().GetRequiredService<PluginRegistry>();
        // What the host would register for it (its operator settings among them).
        var services = new ServiceCollection();
        foreach (var plugin in installed)
        {
            plugin.ConfigureServices(services, PluginHost.Empty());
        }

        registry.FindContract("guestbook.entries@1").Should().NotBeNull();
        registry.InterceptorsOf("forms.submitting").Should().Contain(i => i.PluginId == "sample-guestbook");

        // The built-in Forms plugin runs its hook; the installed plugin refuses the spam.
        var tenant = Guid.NewGuid();
        var factory = new PluginContextFactory(registry, new FakeInstanceStore(
                Instances.Of(tenant, "forms", "contact"),
                Instances.Of(tenant, "sample-guestbook", "guests", new { blockedWords = new[] { "casino" } })),
            services.BuildServiceProvider());
        var forms = await factory.CreateAsync(tenant, "forms", null, PluginActor.Anonymous, TestContext.Current.CancellationToken);

        var data = new Dictionary<string, JsonElement> { ["message"] = JsonSerializer.SerializeToElement("Win at the CASINO") };
        var outcome = await forms.Hooks.RunAsync(new FormSubmitting(Guid.NewGuid(), "contact", data, null), TestContext.Current.CancellationToken);

        outcome.Cancelled.Should().BeTrue();
        outcome.CancelledBy.Should().Be("sample-guestbook");
    }

    [Fact]
    public void A_folder_that_is_not_a_published_plugin_stops_startup()
    {
        var dir = Directory.CreateTempSubdirectory("dcms-plugins-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "broken"));

            var act = () => PluginLoader.LoadFrom(dir.FullName);

            act.Should().Throw<InvalidOperationException>().WithMessage("*broken*deps.json*");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void No_directory_means_no_installed_plugins() =>
        PluginLoader.LoadFrom(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).Should().BeEmpty();
}
