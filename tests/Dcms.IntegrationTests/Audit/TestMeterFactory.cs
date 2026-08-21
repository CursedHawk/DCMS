using System.Diagnostics.Metrics;

namespace Dcms.IntegrationTests.Audit;

/// <summary>
/// The smallest thing that satisfies <see cref="AuditMetrics"/>'s constructor.
///
/// <para>Nothing collects from these meters here; they exist so the code under test can count
/// without the test having to stand up the metrics hosting stack.</para>
/// </summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options.Name, options.Version, options.Tags, scope: this);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (var meter in _meters)
        {
            meter.Dispose();
        }
        _meters.Clear();
    }
}
