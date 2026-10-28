using System.Text.Json.Nodes;
using Dcms.Shared.Data.Tenancy;

namespace Dcms.UnitTests.Data;

/// <summary>
/// The three-way merge a migration applies with (ADR 0026): only what the migration changed lands,
/// whatever else the target or the source did to the same item.
/// </summary>
public class JsonMergeTests
{
    private static (string? Merged, List<MergeConflict> Conflicts) Merge(string @base, string ours, string theirs, Func<string, string?>? choose = null)
    {
        var (merged, conflicts) = JsonMerge.ThreeWay(JsonNode.Parse(@base), JsonNode.Parse(ours), JsonNode.Parse(theirs), choose);
        return (merged?.ToJsonString(), conflicts);
    }

    [Fact]
    public void Each_side_keeps_the_properties_only_it_changed()
    {
        var (merged, conflicts) = Merge("""{"a":1,"b":1}""", """{"a":2,"b":1}""", """{"a":1,"b":3}""");
        conflicts.Should().BeEmpty();
        merged.Should().Be("""{"a":2,"b":3}""");
    }

    [Fact]
    public void Tables_and_fields_merge_by_their_key_so_a_feature_adds_only_its_field()
    {
        // Base: one table with one field. The target (production) renamed the table; the feature
        // added a field; neither touched what the other did.
        const string @base = """{"tables":[{"id":"t1","apiName":"deals","fields":[{"id":"f1","apiName":"name"}]}]}""";
        const string ours = """{"tables":[{"id":"t1","apiName":"deals2","fields":[{"id":"f1","apiName":"name"}]}]}""";
        const string theirs = """{"tables":[{"id":"t1","apiName":"deals","fields":[{"id":"f1","apiName":"name"},{"id":"f2","apiName":"amount"}]}]}""";
        var (merged, conflicts) = Merge(@base, ours, theirs);
        conflicts.Should().BeEmpty();
        merged.Should().Be("""{"tables":[{"id":"t1","apiName":"deals2","fields":[{"id":"f1","apiName":"name"},{"id":"f2","apiName":"amount"}]}]}""");
    }

    [Fact]
    public void A_keyed_item_removed_by_theirs_goes_and_one_removed_by_ours_stays_gone()
    {
        var (merged, conflicts) = Merge(
            """[{"key":"a"},{"key":"b"},{"key":"c"}]""",
            """[{"key":"a"},{"key":"b"}]""",
            """[{"key":"b"},{"key":"c"}]""");
        conflicts.Should().BeEmpty();
        merged.Should().Be("""[{"key":"b"}]""");
    }

    [Fact]
    public void Permissions_merge_as_sets()
    {
        var (merged, conflicts) = Merge(
            """{"Permissions":["a","b"]}""",
            """{"Permissions":["a","b","x"]}""",
            """{"Permissions":["b","y"]}""");
        conflicts.Should().BeEmpty();
        merged.Should().Be("""{"Permissions":["b","x","y"]}""");
    }

    [Fact]
    public void A_list_both_sides_reordered_is_a_conflict_not_a_guess()
    {
        var (_, conflicts) = Merge("""{"columns":["a","b","c"]}""", """{"columns":["c","b","a"]}""", """{"columns":["b","a","c","d"]}""");
        conflicts.Select(c => c.Path).Should().Equal("/columns");
    }

    [Fact]
    public void Both_changing_one_field_differently_is_a_conflict_at_that_path_only()
    {
        var (merged, conflicts) = Merge(
            """{"tables":[{"id":"t1","label":"Deals","color":"red"}]}""",
            """{"tables":[{"id":"t1","label":"Our deals","color":"red"}]}""",
            """{"tables":[{"id":"t1","label":"Their deals","color":"blue"}]}""");
        conflicts.Should().ContainSingle().Which.Path.Should().Be("/tables/t1/label");
        merged.Should().Be("""{"tables":[{"id":"t1","label":"Our deals","color":"blue"}]}""", "an open conflict keeps ours, and the rest merges");
    }

    [Fact]
    public void A_choice_settles_the_conflict_at_its_path()
    {
        var (merged, conflicts) = Merge("""{"a":1}""", """{"a":2}""", """{"a":3}""", path => path == "/a" ? "theirs" : null);
        conflicts.Should().BeEmpty();
        merged.Should().Be("""{"a":3}""");
    }

    [Fact]
    public void Rolling_back_is_the_same_merge_with_the_sides_swapped()
    {
        // The migration added field f2; the target has since added f3 itself. Undoing the
        // migration removes f2 and keeps f3.
        const string before = """{"fields":[{"id":"f1"}]}""";
        const string after = """{"fields":[{"id":"f1"},{"id":"f2"}]}""";
        const string now = """{"fields":[{"id":"f1"},{"id":"f2"},{"id":"f3"}]}""";
        var (merged, conflicts) = Merge(after, now, before);
        conflicts.Should().BeEmpty();
        merged.Should().Be("""{"fields":[{"id":"f1"},{"id":"f3"}]}""");
    }
}
