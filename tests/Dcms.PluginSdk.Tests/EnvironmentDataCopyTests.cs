using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;

namespace Dcms.PluginSdk.Tests;

public class EnvironmentDataCopyTests
{
    private static EnvironmentDataCopy Copy(Dictionary<Guid, Guid>? instances = null) => new()
    {
        TenantId = Guid.NewGuid(),
        SourceEnvironmentId = Guid.NewGuid(),
        TargetEnvironmentId = Guid.NewGuid(),
        Instances = instances ?? [],
        InSource = () => new MemoryStream(),
        InTarget = () => new MemoryStream(),
    };

    [Fact]
    public void Remap_replaces_every_remapped_id_wherever_it_sits_and_nothing_else()
    {
        var (instance, there) = (Guid.NewGuid(), Guid.NewGuid());
        var copy = Copy(new() { [instance] = there });
        var record = Guid.NewGuid();
        var renamed = copy.NewId(record);
        var untouched = Guid.NewGuid();

        var json = copy.Remap($$"""{"company":"{{record}}","tags":["{{record}}","{{untouched}}"],"app":{"id":"{{instance}}"},"n":3,"text":"{{record}} and more"}""");

        var node = JsonNode.Parse(json)!;
        node["company"]!.GetValue<string>().Should().Be(renamed.ToString());
        node["tags"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal(renamed.ToString(), untouched.ToString());
        node["app"]!["id"]!.GetValue<string>().Should().Be(there.ToString());
        node["n"]!.GetValue<int>().Should().Be(3);
        node["text"]!.GetValue<string>().Should().Be($"{record} and more", "only a whole value is an id");
    }

    [Fact]
    public void NewId_is_stable_per_source_id()
    {
        var copy = Copy();
        var id = Guid.NewGuid();
        copy.NewId(id).Should().Be(copy.NewId(id)).And.NotBe(id);
        copy.Map(id).Should().Be(copy.NewId(id));
    }
}
