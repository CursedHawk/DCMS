using Dcms.Plugins.Functions.Runtime;

namespace Dcms.IntegrationTests.Functions;

/// <summary>on.schedule's cron: what fires when, and the 5-minute floor the manifest enforces.</summary>
public sealed class CronTests
{
    [Theory]
    [InlineData("*/15 * * * *", "2026-10-30T10:45:00", true)]
    [InlineData("*/15 * * * *", "2026-10-30T10:46:00", false)]
    [InlineData("0 9 * * 1-5", "2026-10-30T09:00:00", true)] // a Friday
    [InlineData("0 9 * * 1-5", "2026-11-01T09:00:00", false)] // a Sunday
    [InlineData("0 9 1 * 0", "2026-11-01T09:00:00", true)] // day or weekday, as cron does
    [InlineData("30 2 * 1,6 *", "2026-06-10T02:30:00", true)]
    public void Matches(string expression, string at, bool fires) =>
        Cron.TryParse(expression)!.Matches(DateTime.Parse(at, null, System.Globalization.DateTimeStyles.AdjustToUniversal)).Should().Be(fires);

    [Theory]
    [InlineData("* * * * *", 1)]
    [InlineData("*/5 * * * *", 5)]
    [InlineData("0,3 * * * *", 3)]
    [InlineData("0 */2 * * *", 120)]
    public void Shortest_gap(string expression, int minutes) => Cron.TryParse(expression)!.MinGapMinutes().Should().Be(minutes);

    [Theory]
    [InlineData("* * *")]
    [InlineData("60 * * * *")]
    [InlineData("5-1 * * * *")]
    [InlineData("*/0 * * * *")]
    public void Refuses(string expression) => Cron.TryParse(expression).Should().BeNull();
}
