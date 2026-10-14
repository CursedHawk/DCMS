using System.Linq.Expressions;
using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Data;
using Dcms.Shared.Kernel.Views;

namespace Dcms.PluginSdk.Tests;

/// <summary>#278: a view's filter as a LINQ Where over the columns a data set names.</summary>
public sealed class DataQueryFilterTests
{
    private sealed record Row(string Name, string? Note, int Count, DateTimeOffset At, bool Done);

    private static readonly Row[] Rows =
    [
        new("Ann", "vip", 5, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), true),
        new("Bob", null, 12, DateTimeOffset.Parse("2026-06-01T00:00:00Z"), false),
        new("Cyd", "late", 0, DateTimeOffset.Parse("2026-09-01T00:00:00Z"), false),
    ];

    private static readonly Dictionary<string, LambdaExpression> Columns = new()
    {
        ["name"] = (Expression<Func<Row, string>>)(r => r.Name),
        ["note"] = (Expression<Func<Row, string?>>)(r => r.Note),
        ["count"] = (Expression<Func<Row, int>>)(r => r.Count),
        ["at"] = (Expression<Func<Row, DateTimeOffset>>)(r => r.At),
        ["done"] = (Expression<Func<Row, bool>>)(r => r.Done),
    };

    private static string[] Names(string json) =>
        [.. DataQueryFilter.Apply(Rows.AsQueryable(), JsonSerializer.Deserialize<ViewFilter>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Columns).Select(r => r.Name)];

    [Fact]
    public void Comparisons_groups_and_negation_select_the_rows()
    {
        Names("""{"field":"name","op":"contains","value":"N"}""").Should().Equal("Ann");
        Names("""{"field":"count","op":"gte","value":5}""").Should().Equal("Ann", "Bob");
        Names("""{"field":"at","op":"lt","value":"2026-07-01"}""").Should().Equal("Ann", "Bob");
        Names("""{"field":"done","op":"eq","value":false}""").Should().Equal("Bob", "Cyd");
        Names("""{"field":"note","op":"isNull","value":true}""").Should().Equal("Bob");
        Names("""{"field":"name","op":"in","value":["Cyd","Ann"]}""").Should().Equal("Ann", "Cyd");
        Names("""{"or":[{"field":"count","op":"gt","value":10},{"and":[{"field":"done","op":"eq","value":true},{"field":"note","op":"startsWith","value":"V"}]}]}""")
            .Should().Equal("Ann", "Bob");
        Names("""{"not":{"field":"name","op":"eq","value":"Bob"}}""").Should().Equal("Ann", "Cyd");
    }

    [Fact]
    public void A_column_not_named_or_a_value_of_the_wrong_type_is_refused()
    {
        var act = () => Names("""{"field":"secret","op":"eq","value":"x"}""");
        act.Should().Throw<ContractValidationException>();
        var wrong = () => Names("""{"field":"count","op":"eq","value":"many"}""");
        wrong.Should().Throw<ContractValidationException>();
    }
}
