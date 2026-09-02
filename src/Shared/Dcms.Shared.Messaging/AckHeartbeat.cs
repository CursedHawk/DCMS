using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;

namespace Dcms.Shared.Messaging;

/// <summary>
/// Holds a JetStream message's redelivery lease open while a long job runs, by sending
/// periodic in-progress acks.
///
/// <para><b>Why this exists.</b> A consumer's <c>AckWait</c> is a deadline, not a hint: when
/// it passes with no ack, JetStream assumes the worker died and hands the message to someone
/// else. The server default is <b>30 seconds</b>, and a site build or a video transcode takes
/// minutes — so a perfectly healthy job was redelivered every 30s and the same work ran three
/// or four times over. Nothing failed, nothing logged, and the only outward sign was that the
/// downstream event was published once per run. That is how one site publish produced three
/// "your site is live" notifications.</para>
///
/// <para>Raising <c>AckWait</c> alone trades one problem for another: a genuinely dead worker
/// then holds the message for the whole window. A heartbeat separates the two questions — the
/// deadline stays short enough to notice a dead worker, and a live one keeps renewing it for
/// as long as it is actually working.</para>
///
/// <para>Use it as <c>await using var _ = AckHeartbeat.Start(msg, interval, logger);</c> around
/// the work, with an interval comfortably under the consumer's <c>AckWait</c> — a third of it
/// is a good default, so a single dropped beat is not fatal.</para>
/// </summary>
public sealed class AckHeartbeat : IAsyncDisposable
{
    private readonly CancellationTokenSource cts = new();
    private readonly Task loop;

    private AckHeartbeat(Func<CancellationToken, ValueTask> beat, TimeSpan interval, ILogger logger)
    {
        loop = RunAsync(beat, interval, logger, cts.Token);
    }

    /// <summary>Starts renewing <paramref name="msg"/>'s lease every <paramref name="interval"/>.</summary>
    public static AckHeartbeat Start<T>(INatsJSMsg<T> msg, TimeSpan interval, ILogger logger) =>
        new(ct => msg.AckProgressAsync(cancellationToken: ct), interval, logger);

    private static async Task RunAsync(
        Func<CancellationToken, ValueTask> beat, TimeSpan interval, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                await beat(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The work finished (or the host is stopping). Expected.
        }
        catch (Exception ex)
        {
            // Never fail the job over a failed heartbeat. The worst case is the redelivery this
            // exists to prevent, and every handler that uses it already tolerates one.
            logger.LogDebug(ex, "JetStream progress ack failed; the message may be redelivered.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await cts.CancelAsync();
        try
        {
            await loop;
        }
        catch
        {
            // RunAsync swallows its own failures; this only ever sees cancellation.
        }
        cts.Dispose();
    }
}
