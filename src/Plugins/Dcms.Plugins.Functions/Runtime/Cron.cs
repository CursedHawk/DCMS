namespace Dcms.Plugins.Functions.Runtime;

/// <summary>
/// A five-field cron expression (minute hour day month weekday, UTC): <c>*</c>, values, ranges,
/// lists and <c>/step</c>. As in cron, when both day and weekday are restricted either may match.
/// </summary>
public sealed class Cron
{
    private readonly bool[] _minute = new bool[60];
    private readonly bool[] _hour = new bool[24];
    private readonly bool[] _day = new bool[32];
    private readonly bool[] _month = new bool[13];
    private readonly bool[] _weekday = new bool[7];
    private bool _anyDay;
    private bool _anyWeekday;

    private Cron()
    {
    }

    public static Cron? TryParse(string expression)
    {
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) return null;
        var cron = new Cron { _anyDay = fields[2] == "*", _anyWeekday = fields[4] == "*" };
        return Field(fields[0], 0, 59, cron._minute) && Field(fields[1], 0, 23, cron._hour) && Field(fields[2], 1, 31, cron._day)
               && Field(fields[3], 1, 12, cron._month) && Field(fields[4], 0, 7, cron._weekday)
            ? cron
            : null;
    }

    public bool Matches(DateTime utc)
    {
        if (!_minute[utc.Minute] || !_hour[utc.Hour] || !_month[utc.Month]) return false;
        var day = _day[utc.Day];
        var weekday = _weekday[(int)utc.DayOfWeek];
        return _anyDay || _anyWeekday ? day && weekday : day || weekday;
    }

    /// <summary>The shortest time between two firings within a day; int.MaxValue when it fires at most once a day.</summary>
    public int MinGapMinutes()
    {
        var times = Enumerable.Range(0, 24 * 60).Where(m => _hour[m / 60] && _minute[m % 60]).ToList();
        if (times.Count < 2) return int.MaxValue;
        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).Append(times[0] + 24 * 60 - times[^1]);
        return gaps.Min();
    }

    private static bool Field(string text, int min, int max, bool[] into)
    {
        foreach (var item in text.Split(','))
        {
            var (range, step) = item.Split('/') switch
            {
                [var r] => (r, 1),
                [var r, var s] when int.TryParse(s, out var n) && n > 0 => (r, n),
                _ => (null, 0),
            };
            if (range is null) return false;
            int from, to;
            if (range == "*")
            {
                (from, to) = (min, max);
            }
            else if (range.Split('-') is [var a, var b] && int.TryParse(a, out from) && int.TryParse(b, out to))
            {
            }
            else if (int.TryParse(range, out from))
            {
                to = step > 1 ? max : from;
            }
            else
            {
                return false;
            }
            if (from < min || to > max || from > to) return false;
            for (var v = from; v <= to; v += step)
            {
                // Weekday 7 is Sunday, as 0 is.
                into[into.Length == 7 && v == 7 ? 0 : v] = true;
            }
        }
        return true;
    }
}
