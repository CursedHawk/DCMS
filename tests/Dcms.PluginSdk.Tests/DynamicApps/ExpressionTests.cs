using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Automation;

namespace Dcms.PluginSdk.Tests.DynamicApps;

/// <summary>The flow expression language: what it computes, and everything it refuses to do.</summary>
public sealed class ExpressionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, JsonNode?> Scope = new()
    {
        ["row"] = JsonNode.Parse("""{ "amount": 12500, "status": "won", "title": "Big deal", "owner": null, "tags": ["a", "b"], "closes": "2026-10-31" }"""),
        ["previous"] = JsonNode.Parse("""{ "status": "lead" }"""),
        ["changedFields"] = JsonNode.Parse("""["status"]"""),
        ["steps"] = JsonNode.Parse("""{ "find_owner": { "email": "ada@example.com", "name": "Ada" } }"""),
    };

    private static JsonNode? Eval(string source) => Expressions.Evaluate(source, Scope, Now);

    private static readonly System.Text.Json.JsonSerializerOptions Plain =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Theory]
    [InlineData("row.amount >= 10000 && \"status\" in changedFields", "true")]
    [InlineData("row.amount * 2 - 5 / 5", "24999")]
    [InlineData("(1 + 2) * 3 == 9", "true")]
    [InlineData("1 + 2 * 3", "7")]
    [InlineData("-row.amount", "-12500")]
    [InlineData("!(row.status == 'won')", "false")]
    [InlineData("previous.status != row.status", "true")]
    [InlineData("row.status == 'won' ? 'yes' : 'no'", "\"yes\"")]
    [InlineData("row.missing == null", "true")]
    [InlineData("row.missing.deeper", "null")]
    [InlineData("row.tags[1]", "\"b\"")]
    [InlineData("row['title']", "\"Big deal\"")]
    [InlineData("'a' in row.tags && 'c' in row.tags", "false")]
    [InlineData("'Hello ' + steps.find_owner.name", "\"Hello Ada\"")]
    [InlineData("'deal' in row.title && !('Deal' in row.title)", "true")]
    [InlineData("row.closes < '2026-11-01'", "true")]
    [InlineData("row.owner || 'nobody'", "true")]
    [InlineData("coalesce(row.owner, steps.find_owner.email)", "\"ada@example.com\"")]
    [InlineData("len(row.tags) + len('abc')", "5")]
    [InlineData("upper(trim('  x '))", "\"X\"")]
    [InlineData("contains(row.title, 'BIG') && startsWith(row.title, 'big') && endsWith(row.title, 'DEAL')", "true")]
    [InlineData("round(10 / 3, 2)", "3.33")]
    [InlineData("number('42') + 1", "43")]
    [InlineData("today()", "\"2026-10-06\"")]
    [InlineData("addDays(today(), 7)", "\"2026-10-13\"")]
    [InlineData("addHours(now(), 2)", "\"2026-10-06T14:00:00.000Z\"")]
    [InlineData("join(row.tags, '+')", "\"a+b\"")]
    [InlineData("[1, 'a', null]", "[1,\"a\",null]")]
    [InlineData("row.missing + 1", "null")]
    public void Evaluates(string source, string expected)
    {
        (Eval(source)?.ToJsonString(Plain) ?? "null").Should().Be(expected);
    }

    [Theory]
    [InlineData("row.amount >=", "ends too soon")]
    [InlineData("row.amount = 5", "Unexpected '='")]
    [InlineData("'open", "not closed")]
    [InlineData("system('rm -rf /')", "Unknown function 'system'")]
    [InlineData("row.GetType()", "Unexpected '('")]
    [InlineData("secrets.key", "Unknown name 'secrets'")]
    [InlineData("1 / 0", "Division by zero")]
    [InlineData("row.title * 2", "is not a number")]
    [InlineData("len()", "takes 1 argument")]
    public void Refuses(string source, string reason)
    {
        var act = () => Eval(source);
        act.Should().Throw<ExpressionException>().Which.Message.Should().Contain(reason);
    }

    [Fact]
    public void Size_depth_and_work_are_bounded()
    {
        var tooLong = () => Expressions.Parse(new string('1', Expressions.MaxSourceChars + 1));
        tooLong.Should().Throw<ExpressionException>().Which.Message.Should().Contain("characters");

        var tooMany = () => Expressions.Parse(string.Join("+", Enumerable.Repeat("1", 300)));
        tooMany.Should().Throw<ExpressionException>().Which.Message.Should().Contain("parts");

        var tooDeep = () => Expressions.Parse(new string('(', 100) + "1" + new string(')', 100));
        tooDeep.Should().Throw<ExpressionException>().Which.Message.Should().Contain("nests");

        // A short expression over large data cannot build an unbounded string.
        var data = new Dictionary<string, JsonNode?>
        {
            ["big"] = new JsonArray(Enumerable.Range(0, 100).Select(_ => (JsonNode)new string('x', 1_000)).ToArray()),
        };
        var big = () => Expressions.Evaluate("join(big, '')", data, Now);
        big.Should().Throw<ExpressionException>().Which.Message.Should().Contain("longer than");
    }

    [Fact]
    public void Many_holes_cannot_render_an_unbounded_input()
    {
        var data = new Dictionary<string, JsonNode?> { ["big"] = new string('x', 60_000) };
        var template = new JsonObject
        {
            ["parts"] = new JsonArray(Enumerable.Range(0, 10).Select(_ => (JsonNode)"{{ big }}").ToArray()),
        };
        var act = () => Expressions.Render(template, data, Now);
        act.Should().Throw<ExpressionException>().Which.Message.Should().Contain("larger than");
    }

    [Fact]
    public void Values_put_into_html_are_escaped_and_the_template_markup_kept()
    {
        var data = new Dictionary<string, JsonNode?> { ["row"] = JsonNode.Parse("""{ "name": "<a href=\"https://evil.test\">Click</a>" }""") };
        var input = JsonNode.Parse("""{ "html": "<p>Hello <b>{{ row.name }}</b></p>", "whole": "{{ row.name }}", "subject": "Hi {{ row.name }}" }""");

        var rendered = Expressions.Render(input, data, Now, new HashSet<string> { "html", "whole" })!;

        rendered["html"]!.GetValue<string>().Should().Be("<p>Hello <b>&lt;a href=&quot;https://evil.test&quot;&gt;Click&lt;/a&gt;</b></p>");
        rendered["whole"]!.GetValue<string>().Should().StartWith("&lt;a", "a whole-value hole in html is escaped too");
        rendered["subject"]!.GetValue<string>().Should().Contain("<a href", "only the keys named as html are escaped");
        ActionCatalog.Find("dcms.email.send@1")!.HtmlInputs.Should().Contain("html");
    }

    [Fact]
    public void A_list_literal_holds_plain_values_so_it_cannot_multiply_a_large_value()
    {
        var data = new Dictionary<string, JsonNode?> { ["big"] = new JsonArray(Enumerable.Range(0, 1000).Select(i => (JsonNode)i).ToArray()) };
        var act = () => Expressions.Evaluate("[big, big, big]", data, Now);
        act.Should().Throw<ExpressionException>().Which.Message.Should().Contain("plain values");
        Expressions.Evaluate("[1, 'a', null, true]", data, Now)!.AsArray().Should().HaveCount(4);
    }

    [Fact]
    public void Templates_keep_types_for_a_whole_value_and_interpolate_otherwise()
    {
        var input = JsonNode.Parse("""
            { "to": "{{ steps.find_owner.email }}", "amount": "{{ row.amount }}", "subject": "Deal {{ row.title }} ({{ row.amount }})",
              "list": ["{{ row.tags }}", "plain"], "nested": { "flag": "{{ row.amount > 1 }}" } }
            """);

        var rendered = Expressions.Render(input, Scope, Now)!;

        rendered["to"]!.GetValue<string>().Should().Be("ada@example.com");
        rendered["amount"]!.GetValue<decimal>().Should().Be(12500, "a whole-value hole keeps the number a number");
        rendered["subject"]!.GetValue<string>().Should().Be("Deal Big deal (12500)");
        rendered["list"]![0]!.AsArray().Should().HaveCount(2);
        rendered["nested"]!["flag"]!.GetValue<bool>().Should().BeTrue();
        Expressions.TemplateExpressions(input).Should().Contain(["steps.find_owner.email", "row.amount > 1"]);
    }

    [Fact]
    public void Variables_lists_what_an_expression_reads()
    {
        Expressions.Variables(Expressions.Parse("row.a > 1 && coalesce(steps.x.y, previous.z) in changedFields"))
            .Distinct().Should().BeEquivalentTo(["row", "steps", "previous", "changedFields"]);
    }
}
