using System.Text.Json;
using Dcms.PluginSdk.Runtime;
using Dcms.Plugins.WebSockets;
using Dcms.Plugins.WebSockets.Api;

namespace Dcms.PluginSdk.Tests;

/// <summary>
/// The WebSockets plugin's channel config (ADR 0037): the payload-schema subset the config schema
/// enforces, the checks every send and publish goes through, and the group names that keep
/// tenants, environments and instances apart.
/// </summary>
public class WebSocketsPluginTests
{
    private const string Config = """
        {
          "channels": [
            {
              "name": "chat", "keyed": true, "subscribe": "signedIn", "publish": "subscribers", "raiseEvent": true,
              "schema": {
                "type": "object",
                "properties": {
                  "text": { "type": "string", "maxLength": 500 },
                  "kind": { "enum": ["text", "image"] },
                  "tags": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["text"],
                "additionalProperties": false
              }
            },
            { "name": "notify", "keyed": true, "subscribe": "self" },
            { "name": "vip", "subscribe": "permission", "publish": "permission" },
            { "name": "chat", "subscribe": "public" }
          ]
        }
        """;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static readonly IReadOnlyList<ChannelDefinition> Read = Channels.Read(Json(Config));

    private static ChannelDefinition Channel(string name) => Read.Single(c => c.Name == name);

    [Fact]
    public void A_config_with_the_supported_schema_subset_is_valid()
    {
        var (valid, errors) = new PluginConfigValidator().Validate(new WebSocketsPlugin().Manifest.ConfigJsonSchema, Config);

        valid.Should().BeTrue(string.Join("\n", errors));
    }

    [Theory]
    [InlineData("""{ "$ref": "#/x" }""")]
    [InlineData("""{ "oneOf": [{ "type": "string" }] }""")]
    [InlineData("""{ "type": ["string", "null"] }""")]
    [InlineData("""{ "type": "object", "properties": { "a": { "pattern": "(a+)+$" } } }""")]
    public void A_payload_schema_outside_the_subset_is_refused(string schema)
    {
        var config = $$"""{ "channels": [ { "name": "x", "schema": {{schema}} } ] }""";

        var (valid, _) = new PluginConfigValidator().Validate(new WebSocketsPlugin().Manifest.ConfigJsonSchema, config);

        valid.Should().BeFalse("the builder and both generators can only type the subset");
    }

    [Fact]
    public void A_repeated_channel_name_keeps_its_first_definition()
    {
        Read.Select(c => c.Name).Should().Equal("chat", "notify", "vip");
        Channel("chat").Keyed.Should().BeTrue();
    }

    [Fact]
    public void Keys_must_match_whether_the_channel_is_keyed()
    {
        Channels.CheckKey(Channel("chat"), null).Should().NotBeNull();
        Channels.CheckKey(Channel("chat"), "room-42").Should().BeNull();
        Channels.CheckKey(Channel("chat"), "../other").Should().NotBeNull();
        Channels.CheckKey(Channel("vip"), "x").Should().NotBeNull();
        Channels.CheckKey(Channel("vip"), null).Should().BeNull();
    }

    [Fact]
    public void Payloads_are_checked_against_the_channel_schema()
    {
        Channels.CheckPayload(Channel("chat"), Json("""{ "text": "hi", "kind": "text" }""")).Should().BeNull();
        Channels.CheckPayload(Channel("chat"), Json("""{ "kind": "text" }""")).Should().NotBeNull();
        Channels.CheckPayload(Channel("chat"), Json("""{ "text": "hi", "extra": 1 }""")).Should().NotBeNull();
        // No schema: any JSON, within the size cap.
        Channels.CheckPayload(Channel("vip"), Json("""[1, "two"]""")).Should().BeNull();
        Channels.CheckPayload(Channel("vip"), Json($"\"{new string('x', Channels.MaxPayloadBytes)}\"")).Should().NotBeNull();
    }

    private static ConnectionState Caller(Guid? user = null, params string[] permissions) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "rt") { UserId = user, Permissions = permissions.ToHashSet() };

    [Fact]
    public void Signed_in_channels_refuse_anonymous_callers()
    {
        Access.CheckSubscribe(Channel("chat"), Caller(), "room").Should().NotBeNull();
        Access.CheckSubscribe(Channel("chat"), Caller(Guid.NewGuid()), "room").Should().BeNull();
        Access.CheckPublish(Channel("chat"), Caller(), "room").Should().NotBeNull();
        Access.CheckPublish(Channel("chat"), Caller(Guid.NewGuid()), "room").Should().BeNull();
    }

    [Fact]
    public void A_self_channel_admits_only_the_callers_own_key()
    {
        var me = Guid.NewGuid();
        Access.CheckSubscribe(Channel("notify"), Caller(me), me.ToString()).Should().BeNull();
        Access.CheckSubscribe(Channel("notify"), Caller(me), Guid.NewGuid().ToString()).Should().NotBeNull();
        Access.CheckSubscribe(Channel("notify"), Caller(), me.ToString()).Should().NotBeNull();
        Access.CheckPublish(Channel("notify"), Caller(me), me.ToString()).Should().NotBeNull("only the server publishes on it");
    }

    [Fact]
    public void Permission_channels_need_the_site_permission_for_this_instance()
    {
        var user = Guid.NewGuid();
        Access.CheckSubscribe(Channel("vip"), Caller(user), null).Should().NotBeNull();
        Access.CheckSubscribe(Channel("vip"), Caller(user, "websockets:rt:channel:vip:subscribe"), null).Should().BeNull();
        Access.CheckSubscribe(Channel("vip"), Caller(user, "websockets:other:channel:vip:subscribe"), null).Should().NotBeNull();
        Access.CheckPublish(Channel("vip"), Caller(user, "websockets:rt:channel:vip:subscribe"), null).Should().NotBeNull();
        Access.CheckPublish(Channel("vip"), Caller(user, "websockets:rt:channel:vip:publish"), null).Should().BeNull();
    }

    [Fact]
    public void Group_names_differ_by_tenant_environment_instance_channel_and_key()
    {
        var (t, e) = (Guid.NewGuid(), Guid.NewGuid());
        var names = new[]
        {
            ChannelGroups.Channel(t, e, "rt", "chat", "a"),
            ChannelGroups.Channel(Guid.NewGuid(), e, "rt", "chat", "a"),
            ChannelGroups.Channel(t, Guid.NewGuid(), "rt", "chat", "a"),
            ChannelGroups.Channel(t, e, "rt2", "chat", "a"),
            ChannelGroups.Channel(t, e, "rt", "chat", "b"),
            ChannelGroups.Channel(t, e, "rt", "chat", null),
            ChannelGroups.User(t, e, "rt", Guid.NewGuid()),
        };
        names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_version_changes_when_a_channel_changes_and_not_otherwise()
    {
        var before = Channels.Version(Json(Config));
        Channels.Version(Json(Config)).Should().Be(before);
        Channels.Version(Json(Config.Replace("\"maxLength\": 500", "\"maxLength\": 600"))).Should().NotBe(before);
    }

    [Fact]
    public void With_replaces_the_channel_list_and_keeps_other_keys()
    {
        var updated = Channels.With(Json("""{ "other": 1, "channels": [] }"""), [new ChannelDefinition("news")]);

        updated.GetProperty("other").GetInt32().Should().Be(1);
        Channels.Read(updated).Select(c => c.Name).Should().Equal("news");
        updated.GetProperty("channels")[0].TryGetProperty("description", out _).Should().BeFalse("nulls are not stored");
    }
}
