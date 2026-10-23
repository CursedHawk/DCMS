using System.Diagnostics;
using Dcms.Shared.Telemetry;

namespace Dcms.UnitTests.Telemetry;

/// <summary>
/// The verdict per tag name is memoised (2026-10-23), so the second span carrying a name must be
/// scrubbed exactly like the first -- a cache that only remembered "not sensitive" would leak.
/// </summary>
public class SensitiveAttributeProcessorTests
{
    [Fact]
    public void Every_span_is_scrubbed_the_same_however_often_a_name_recurs()
    {
        var processor = new SensitiveAttributeProcessor();
        for (var i = 0; i < 3; i++)
        {
            using var span = new Activity("test").Start();
            span.SetTag("http.route", "/api/{slug}");
            span.SetTag("db.Password", "hunter2");
            span.SetTag("url.full", "https://x.test/a?token=abc");

            processor.OnEnd(span);

            span.GetTagItem("http.route").Should().Be("/api/{slug}");
            span.GetTagItem("db.Password").Should().Be("[redacted]");
            span.GetTagItem("url.full").Should().Be("https://x.test/a");
        }
    }
}
