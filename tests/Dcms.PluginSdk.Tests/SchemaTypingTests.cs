using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime.Typing;
using Dcms.Plugins.Functions.Build;
using Dcms.Plugins.WebSockets;

namespace Dcms.PluginSdk.Tests;

/// <summary>
/// The shared JSON Schema → TypeScript converter and the C# record generator (ADR 0037): what the
/// site client, the Functions SDK and the channel types all come out of.
/// </summary>
public class SchemaTypingTests
{
    private static string Ts(string schema, string unknown = "unknown") => new JsonSchemaTs(unknown).Type(JsonNode.Parse(schema));

    [Theory]
    [InlineData("""{ "type": "string" }""", "string")]
    [InlineData("""{ "type": ["string", "null"] }""", "string | null")]
    [InlineData("""{ "type": "integer", "nullable": true }""", "number | null")]
    [InlineData("""{ "const": "chat" }""", "\"chat\"")]
    [InlineData("""{ "enum": ["a", 2, true] }""", "\"a\" | 2 | true")]
    [InlineData("""{ "oneOf": [{ "type": "string" }, { "type": "number" }] }""", "string | number")]
    [InlineData("""{ "allOf": [{ "properties": { "a": { "type": "string" } } }, { "properties": { "b": { "type": "number" } } }] }""", "{ a?: string } & { b?: number }")]
    [InlineData("""{ "type": "array", "items": { "enum": ["x", "y"] } }""", "(\"x\" | \"y\")[]")]
    [InlineData("""{ "type": "object", "additionalProperties": { "type": "boolean" } }""", "Record<string, boolean>")]
    [InlineData("""{ "type": "object", "properties": { "a-b": { "type": "string" } }, "required": ["a-b"] }""", "{ \"a-b\": string }")]
    [InlineData("""{ "anyOf": [{ "type": "string" }, {}] }""", "unknown")]
    [InlineData("""true""", "unknown")]
    [InlineData("""false""", "never")]
    public void Converts_schemas_to_typescript(string schema, string expected) => Ts(schema).Should().Be(expected);

    [Fact]
    public void Special_answers_refs_and_every_nested_node()
    {
        var typer = new JsonSchemaTs("unknown", s => s["$ref"] is JsonValue r ? r.GetValue<string>().Split('/')[^1] : null);

        typer.Type(JsonNode.Parse("""{ "type": "array", "items": { "$ref": "#/components/schemas/Order" } }""")).Should().Be("Order[]");
        typer.Type(JsonNode.Parse("""{ "$ref": "#/components/schemas/Order", "nullable": true }""")).Should().Be("Order | null");
    }

    [Fact]
    public void Too_deep_a_schema_is_unknown_rather_than_wrong()
    {
        var node = """{ "type": "string" }""";
        for (var i = 0; i < 12; i++) node = $$"""{ "type": "array", "items": {{node}} }""";

        Ts(node, "Json").Should().Contain("Json");
    }

    private static (string Type, string Records) Cs(string schema, string hint = "Thing")
    {
        var types = new StringBuilder();
        var type = new JsonSchemaCs([], types).Type(JsonNode.Parse(schema), hint);
        return (type, types.ToString());
    }

    [Fact]
    public void Objects_become_records_with_required_and_optional_members()
    {
        var (type, records) = Cs("""
            { "type": "object", "description": "An order.",
              "properties": {
                "id": { "type": "string" },
                "total": { "type": "number" },
                "lines": { "type": "array", "items": { "type": "object", "properties": { "sku": { "type": "string" } } } },
                "kind": { "const": "order" }
              },
              "required": ["id"] }
            """, "Order");

        type.Should().Be("Order");
        records.Should().Contain("public sealed record Order");
        records.Should().Contain("""[System.Text.Json.Serialization.JsonPropertyName("id")] public required string Id { get; init; }""");
        records.Should().Contain("""public double? Total { get; init; }""");
        records.Should().Contain("""public IReadOnlyList<OrderLinesItem?>? Lines { get; init; }""");
        records.Should().Contain("""public string Kind => "order";""");
        records.Should().Contain("public sealed record OrderLinesItem");
    }

    [Fact]
    public void A_union_of_objects_gives_one_record_per_branch_named_by_its_constant()
    {
        var types = new StringBuilder();
        var branches = new JsonSchemaCs([], types).Branches(JsonNode.Parse("""
            { "oneOf": [
              { "type": "object", "properties": { "channel": { "const": "chat" }, "payload": { "type": "string" } } },
              { "type": "object", "properties": { "channel": { "const": "live-score" }, "payload": { "type": "integer" } } }
            ] }
            """), "RtSend");

        branches.Should().Equal("RtSendChat", "RtSendLiveScore");
    }

    [Fact]
    public void A_websockets_instance_types_send_per_channel()
    {
        var config = JsonDocument.Parse("""
            { "channels": [
              { "name": "chat", "keyed": true, "schema": { "type": "object", "properties": { "text": { "type": "string" } }, "required": ["text"] } },
              { "name": "ping" }
            ] }
            """);
        var instance = new PluginInstanceContext(Guid.NewGuid(), Guid.NewGuid(), "websockets", "rt", "Realtime", "", config);

        var schemas = new WebSocketsPlugin().OperationInputSchemas(instance);

        new JsonSchemaTs("Json").Type(schemas["websockets.channels.Send"]).Should().Be(
            "{ channel: \"chat\"; payload: { text: string }; key: string } | { channel: \"ping\"; payload: Json }");
        new JsonSchemaTs("Json").Type(schemas["websockets.channels.SendToUser"]).Should().Contain("to: string");
    }
}
