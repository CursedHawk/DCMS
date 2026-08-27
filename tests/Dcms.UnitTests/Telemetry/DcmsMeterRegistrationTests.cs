using System.Diagnostics.Metrics;
using System.Reflection;
using Dcms.Shared.Audit;
using Dcms.Shared.Telemetry;
using Dcms.UnitTests.Audit;

namespace Dcms.UnitTests.Telemetry;

/// <summary>
/// Guards the failure that motivated this whole test file: a meter that is defined, documented
/// and never exported.
///
/// <para><c>AuditMetrics</c> shipped six instruments. <c>docs/runbook.md</c> tabulated them with
/// alert thresholds. None of them ever reached a metrics backend, because OpenTelemetry only
/// exports meters that were subscribed by name with <c>AddMeter</c> and that call did not
/// exist. There was no error and no warning — an unsubscribed meter is dropped silently.</para>
///
/// <para>What made it survive a whole release is the part worth pinning. The documented alert
/// for <c>dcms.audit.recorded</c> is that it <b>flat-lines</b>, because a silent audit log looks
/// exactly like an idle platform. A meter exporting nothing produces the same empty series as a
/// platform where nobody did anything. The bug and the incident it was built to catch are
/// indistinguishable from the outside, so nothing could have surfaced it except a test.</para>
/// </summary>
public class DcmsMeterRegistrationTests
{
    [Fact]
    public void Audit_meter_is_in_the_subscription_list()
    {
        DcmsMeters.All.Should().Contain(AuditMetrics.MeterName);
    }

    [Fact]
    public void Platform_meter_is_in_the_subscription_list()
    {
        DcmsMeters.All.Should().Contain(DcmsMetrics.MeterName);
    }

    /// <summary>
    /// The general form: any type in the platform's own assemblies that declares a
    /// <c>MeterName</c> constant is a meter someone intends to export, so it has to be named in
    /// <see cref="DcmsMeters.Dcms"/>. Adding a meter and forgetting the registration is the
    /// exact mistake this reproduces, and it fails at build time rather than in production.
    /// </summary>
    [Fact]
    public void Every_declared_meter_name_is_subscribed()
    {
        var declared = new[] { typeof(AuditMetrics).Assembly, typeof(DcmsMetrics).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Select(type => type.GetField("MeterName", BindingFlags.Public | BindingFlags.Static))
            .Where(field => field is { IsLiteral: true, FieldType: { } t } && t == typeof(string))
            .Select(field => (string)field!.GetRawConstantValue()!)
            .Distinct()
            .ToArray();

        declared.Should().NotBeEmpty("the reflection would otherwise pass by finding nothing");
        declared.Should().BeSubsetOf(DcmsMeters.Dcms);
    }

    /// <summary>
    /// The instrument names the runbook's alert table is written against. Renaming one silently
    /// retires its alert, so the names are pinned here rather than left to be discovered by an
    /// alert that stopped firing.
    /// </summary>
    [Fact]
    public void Audit_instrument_names_match_the_documented_alerts()
    {
        // Filtered on this factory, not on the meter name. MeterListener.Start() republishes
        // every instrument already alive in the process, so another test class holding an
        // undisposed AuditMetrics would contribute a second copy of all six names and turn this
        // into a flake that only appears under parallel execution. TestMeterFactory passes
        // itself as the meter's scope, which is the only handle that distinguishes "the meter
        // this test created" from "a meter with the same name".
        using var factory = new TestMeterFactory();

        var recorded = new List<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory))
                {
                    recorded.Add(instrument.Name);
                }
            },
        };
        listener.Start();

        using var metrics = new AuditMetrics(factory);

        recorded.Should().BeEquivalentTo(
        [
            "dcms.audit.recorded",
            "dcms.audit.sink_failed",
            "dcms.audit.chain_broken",
            "dcms.audit.producer_gap",
            "dcms.audit.writer_lag",
            "dcms.audit.outbox_depth",
        ]);
    }
}
