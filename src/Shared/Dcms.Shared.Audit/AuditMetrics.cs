using System.Diagnostics.Metrics;

namespace Dcms.Shared.Audit;

/// <summary>
/// What an operator watches to know the audit log is alive.
///
/// <para>The point of these is one specific failure: an audit log that has quietly stopped
/// writing is indistinguishable, from the outside, from a platform where nobody did anything.
/// Every other subsystem here fails loudly — a request 500s, a page does not load. This one
/// fails by producing nothing, which looks like success. So the numbers have to be published
/// whether or not anything is wrong, and a gap in them has to be as alarming as a bad value.</para>
///
/// <para>Registered as a singleton and emitted through <c>System.Diagnostics.Metrics</c>, which
/// the existing OpenTelemetry wiring already exports when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is
/// set. Nothing new to run.</para>
/// </summary>
public sealed class AuditMetrics : IDisposable
{
    public const string MeterName = "Dcms.Audit";

    private readonly Meter _meter;
    private readonly Counter<long> _recorded;
    private readonly Counter<long> _sinkFailures;
    private readonly Counter<long> _chainBroken;
    private readonly Counter<long> _producerGaps;
    private readonly Histogram<double> _writerLagSeconds;

    private long _outboxDepth;

    public AuditMetrics(IMeterFactory factory)
    {
        _meter = factory.Create(MeterName);

        _recorded = _meter.CreateCounter<long>(
            "dcms.audit.recorded",
            unit: "{record}",
            description: "Records handed to a sink. Flat-lining is the alarm, not a spike.");

        _sinkFailures = _meter.CreateCounter<long>(
            "dcms.audit.sink_failed",
            unit: "{record}",
            description: "Records the sink refused. Each one now survives only in a log line.");

        _chainBroken = _meter.CreateCounter<long>(
            "dcms.audit.chain_broken",
            unit: "{segment}",
            description: "Segments that failed verification. Any non-zero value is an incident.");

        _producerGaps = _meter.CreateCounter<long>(
            "dcms.audit.producer_gap",
            unit: "{gap}",
            description: "Missing ProducerSeq values: records a process began and never delivered.");

        _writerLagSeconds = _meter.CreateHistogram<double>(
            "dcms.audit.writer_lag",
            unit: "s",
            description: "Age of the oldest unchained outbox row.");

        // Observable rather than a counter: depth is a level, and a gauge read on collection
        // cannot drift from the value the maintenance pass last measured.
        _meter.CreateObservableGauge(
            "dcms.audit.outbox_depth",
            () => Interlocked.Read(ref _outboxDepth),
            unit: "{record}",
            description: "Records written but not yet appended to the chain.");
    }

    public void RecordWritten(int count) => _recorded.Add(count);

    public void RecordSinkFailure(int count) => _sinkFailures.Add(count);

    public void RecordChainBroken() => _chainBroken.Add(1);

    /// <summary>
    /// A hash chain proves nothing about records that were never written. A per-process
    /// sequence is what makes an omission — a dropped buffer, a killed process — visible, and
    /// this is where that visibility becomes an alert.
    /// </summary>
    public void RecordProducerGap(long missing) => _producerGaps.Add(missing);

    public void RecordOutboxDepth(int depth) => Interlocked.Exchange(ref _outboxDepth, depth);

    public void RecordWriterLag(TimeSpan lag) => _writerLagSeconds.Record(lag.TotalSeconds);

    public void Dispose() => _meter.Dispose();
}
