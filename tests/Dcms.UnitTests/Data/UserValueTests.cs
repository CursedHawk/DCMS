using System.Text.Json;
using Dcms.Shared.Kernel.Views;

namespace Dcms.UnitTests.Data;

/// <summary>"The current user" in a view's filter (#302): stored as a token, replaced per viewer.</summary>
public sealed class UserValueTests
{
    private static ViewFilter Parse(string json) => JsonSerializer.Deserialize<ViewFilter>(json, JsonSerializerOptions.Web)!;

    private static string Json(ViewFilter? f) => JsonSerializer.Serialize(f, new JsonSerializerOptions(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    });

    private static readonly ViewUser Ada = new("u-1", "ada@corp.test", ["g-1", "g-2"]);

    [Fact]
    public void Each_viewer_gets_themselves()
    {
        var filter = Parse("""{ "and": [ { "field": "owner", "op": "eq", "value": { "$user": "email" } }, { "field": "team", "op": "in", "value": { "$user": "groups" } }, { "field": "status", "op": "eq", "value": "open" } ] }""");

        UserValue.Mentioned(filter).Should().BeTrue();
        Json(UserValue.Resolve(filter, Ada)).Should().Be(
            """{"and":[{"field":"owner","op":"eq","value":"ada@corp.test"},{"field":"team","op":"in","value":["g-1","g-2"]},{"field":"status","op":"eq","value":"open"}]}""");
        Json(UserValue.Resolve(filter, new ViewUser("u-2", "bob@corp.test", []))).Should().Be(
            """{"and":[{"field":"owner","op":"eq","value":"bob@corp.test"},{"or":[]},{"field":"status","op":"eq","value":"open"}]}""",
            "no groups is a list nothing is in, not an empty list");
    }

    [Fact]
    public void Nobody_is_a_condition_nothing_meets_or_everything_does_when_negated()
    {
        Json(UserValue.Resolve(Parse("""{ "field": "owner", "op": "eq", "value": { "$user": "id" } }"""), null)).Should().Be("""{"or":[]}""");
        Json(UserValue.Resolve(Parse("""{ "not": { "field": "owner", "op": "ne", "value": { "$user": "email" } } }"""), new ViewUser("u", null, [])))
            .Should().Be("""{"not":{"and":[]}}""");
    }

    [Theory]
    [InlineData("""{ "field": "a", "op": "eq", "value": { "$user": "groups" } }""")]
    [InlineData("""{ "field": "a", "op": "gt", "value": { "$user": "email" } }""")]
    [InlineData("""{ "field": "a", "op": "eq", "value": { "$user": "password" } }""")]
    [InlineData("""{ "field": "a", "op": "eq", "value": { "anything": 1 } }""")]
    public void Only_the_current_user_is_an_object_value_and_only_where_it_fits(string json)
    {
        var act = () => Parse(json).Validate();
        act.Should().Throw<ViewDefinitionException>();
    }

    [Fact]
    public void A_plain_filter_is_left_as_it_is()
    {
        var filter = Parse("""{ "field": "a", "op": "in", "value": ["x"] }""");
        UserValue.Mentioned(filter).Should().BeFalse();
        UserValue.Resolve(filter, Ada).Should().BeSameAs(filter);
        filter.Validate();
        Parse("""{ "field": "a", "op": "contains", "value": { "$user": "email" } }""").Validate();
    }
}
