using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Data;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Data;

namespace Dcms.PluginSdk.Tests;

/// <summary>Manifest data sets (ADR 0018): what the registry refuses, and the ones the platform adds.</summary>
public class DataSetTests
{
    private sealed class Rows : IPluginDataSet
    {
        public Task<DataSetSchema> DescribeAsync(CancellationToken ct) => Task.FromResult(new DataSetSchema([]));
        public Task<DataPage> ListAsync(DataQuery query, CancellationToken ct) => Task.FromResult(new DataPage([], 0));
        public Task<DataRow?> GetAsync(string key, CancellationToken ct) => Task.FromResult<DataRow?>(null);
    }

    private sealed class Plugin(params DataSetDeclaration[] sets) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create(
            "sample", "Sample", "", false,
            permissions: [new PermissionDefinition("read", "Read")],
            consumes: [ContractRequirement.Of<IPluginStorage>(), ContractRequirement.Of<IPluginBlobs>()],
            dataSets: sets);
    }

    [Fact]
    public void A_valid_data_set_registers()
    {
        var act = () => new PluginRegistry([new Plugin(
            DataSetDeclaration.Of<Rows>("rows", "Rows", readPermission: "plugin:sample:read", writePermission: "content:write"))]);
        act.Should().NotThrow("its own permission and a platform one are both fine");
    }

    [Theory]
    [InlineData("Rows")]
    [InlineData("dcms.storage")]
    [InlineData("")]
    public void A_data_set_id_must_be_kebab_case(string id)
    {
        var act = () => new PluginRegistry([new Plugin(DataSetDeclaration.Of<Rows>(id, "Rows"))]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*kebab-case*");
    }

    [Fact]
    public void Data_set_ids_are_unique_per_plugin()
    {
        var act = () => new PluginRegistry([new Plugin(DataSetDeclaration.Of<Rows>("rows", "A"), DataSetDeclaration.Of<Rows>("rows", "B"))]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*unique*");
    }

    [Fact]
    public void A_data_set_must_implement_the_interface()
    {
        var act = () => new PluginRegistry([new Plugin(new DataSetDeclaration("rows", "Rows", typeof(string)))]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*IPluginDataSet*");
    }

    [Fact]
    public void A_permission_nobody_declares_is_refused()
    {
        var act = () => new PluginRegistry([new Plugin(DataSetDeclaration.Of<Rows>("rows", "Rows", readPermission: "plugin:other:read"))]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*plugin:other:read*");
    }

    [Fact]
    public void The_platform_adds_sets_for_the_stores_a_plugin_consumes()
    {
        var sets = PluginDataSets.Of(new Plugin(DataSetDeclaration.Of<Rows>("rows", "Rows")).Manifest);
        sets.Select(s => s.Id).Should().Equal("rows", "dcms.storage", "dcms.blobs");

        PluginDataSets.Of(PluginManifest.Create("bare", "Bare", "", false)).Should().BeEmpty();
    }

    [Fact]
    public void Positional_json_schema_survives_on_the_schema_record()
    {
        // The editor is rendered from ItemSchema; it must round-trip as a plain JSON object.
        var schema = new DataSetSchema([new DataColumn("a", "A")], ItemSchema: new JsonObject { ["type"] = "object" });
        System.Text.Json.JsonSerializer.Serialize(schema, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            .Should().Contain("\"itemSchema\":{\"type\":\"object\"}").And.Contain("\"kind\":\"text\"");
    }
}
