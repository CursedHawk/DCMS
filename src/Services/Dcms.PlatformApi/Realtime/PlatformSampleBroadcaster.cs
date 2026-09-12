namespace Dcms.PlatformApi.Realtime;

/// <summary>
/// Takes the period of the console's periodic reads server-side, so no browser has to.
///
/// <para><b>Why this exists.</b> Most of the console became push-driven, but three of its
/// panels read data out of systems that announce nothing: Prometheus for the golden signals and
/// object-store usage, Loki for its delete queue, and the tenant plane for the platform totals.
/// Those kept browser-side <c>refetchInterval</c>s, which meant the load of watching the
/// platform scaled with the number of tabs somebody had left open — five operators with the
/// monitoring page up was five times the Prometheus traffic of one.</para>
///
/// <para>So the timer moves here. The tick carries no data, only the same resource tag a real
/// change would carry, and each console refetches over its ordinary authorised REST call. That
/// keeps one code path in the client for "this is stale now" and keeps the authorisation
/// exactly where it already was.</para>
///
/// <para><b>Nobody watching means no work.</b> The broadcaster does nothing while this replica
/// holds no console connection, so an idle platform samples nothing — which a browser poll
/// could never manage, because a browser only polls when somebody is there and has no way to
/// tell anyone else.</para>
///
/// <para>The periods below are the ones the client used, with one exception: the overview is
/// sampled at its old 30 seconds even though its underlying counts move slowly, because it is
/// the screen people leave open during an incident and a stale total there is read as a
/// fact.</para>
/// </summary>
public sealed class PlatformSampleBroadcaster(
    IPlatformChangePublisher changes,
    ILogger<PlatformSampleBroadcaster> logger) : BackgroundService
{
    /// <summary>
    /// A tag and how often it is worth re-reading.
    ///
    /// <para>Prometheus's own scrape interval is the floor for the two that come from it:
    /// sampling faster than the source is asking a question that cannot have a new answer.</para>
    /// </summary>
    private static readonly (string Tag, TimeSpan Period)[] Samples =
    [
        (PlatformResourceTags.Health, TimeSpan.FromSeconds(30)),
        (PlatformResourceTags.Overview, TimeSpan.FromSeconds(30)),
        (PlatformResourceTags.Stores, TimeSpan.FromSeconds(30)),
    ];

    /// <summary>
    /// The tick the loop runs on. Every period above is a multiple of it, so one timer serves
    /// them all and a tag with a longer period simply fires on fewer ticks.
    /// </summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastSent = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        using var timer = new PeriodicTimer(Tick);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!PlatformHub.AnyConnected)
                {
                    // Forget when each tag was last sent while nobody was listening, so the
                    // first console to connect gets a tick promptly rather than waiting out
                    // the remainder of a period that ran while the room was empty.
                    lastSent.Clear();
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                foreach (var (tag, period) in Samples)
                {
                    if (lastSent.TryGetValue(tag, out var sent) && now - sent < period) continue;
                    lastSent[tag] = now;
                    await changes.PublishAsync(tag, ct: stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            // Losing the sample loop costs freshness on three panels and nothing else; every
            // one of them still loads, still refetches on focus, and still refetches on
            // reconnect. It is not worth taking the API down for.
            logger.LogError(ex, "Platform sample broadcaster stopped.");
        }
    }
}
