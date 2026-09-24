using System.Net;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Edge;
using Microsoft.EntityFrameworkCore;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.Edge.Protection;

/// <summary>
/// The client addresses and ranges the edge does not rate-limit (<c>edge.rate_limit_exemptions</c>,
/// edited in the platform console), held in memory because it is consulted on every request.
///
/// <para>An exempt request is also marked for the services behind the edge with
/// <see cref="Header"/>, so content-api's and site-host's own limiters stand aside for it too.
/// That header is trusted downstream for the same reason <c>X-Forwarded-For</c> already is: the
/// edge strips every inbound copy (<see cref="Mark"/>), so only the edge can
/// have set it, and something already inside the compose network could as easily forge a fresh
/// <c>X-Forwarded-For</c> bucket per request.</para>
/// </summary>
public sealed class RateLimitExemptions
{
    public const string Header = "X-Dcms-RateLimit-Exempt";
    private const string ItemKey = "dcms.ratelimit.exempt";

    private volatile IPNetwork[] _ranges = [];

    public int Count => _ranges.Length;

    public void Replace(IPNetwork[] ranges) => _ranges = ranges;

    public bool IsExempt(IPAddress? address) => RateLimitExemptionRules.Matches(_ranges, address);

    /// <summary>What the limiter asks: was this request found exempt when it arrived?</summary>
    public static bool IsMarked(HttpContext context) => context.Items.ContainsKey(ItemKey);

    /// <summary>Marks the request if its real TCP peer is exempt. Called by the middleware.</summary>
    public void Mark(HttpContext context)
    {
        // Always stripped first: only the edge may say a request is exempt.
        context.Request.Headers.Remove(Header);
        if (IsExempt(context.Connection.RemoteIpAddress))
        {
            context.Items[ItemKey] = true;
            context.Request.Headers[Header] = "1";
        }
    }
}

/// <summary>
/// Keeps <see cref="RateLimitExemptions"/> current without a restart: loads the list at startup,
/// reloads it the moment the console saves a change (<see cref="Subjects.RateLimitExemptionsChanged"/>,
/// a broadcast every replica receives — see <c>EdgeConfigInvalidator</c> for why an ephemeral
/// ordered consumer), and re-reads it every few minutes regardless, so a missed message costs
/// minutes of staleness rather than an exemption that never arrives.
/// </summary>
public sealed class RateLimitExemptionLoader(
    RateLimitExemptions exemptions,
    IServiceScopeFactory scopes,
    INatsJSContext jetStream,
    ILogger<RateLimitExemptionLoader> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    private string _loaded = "";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(SweepAsync(stoppingToken), ListenAsync(stoppingToken));

    private async Task SweepAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await ReloadAsync(ct);
            try { await Task.Delay(SweepInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrderedConsumerAsync(
                    Streams.Tenancy,
                    new NatsJSOrderedConsumerOpts
                    {
                        FilterSubjects = [Subjects.RateLimitExemptionsChanged],
                        DeliverPolicy = ConsumerConfigDeliverPolicy.New,
                    },
                    ct);

                await foreach (var _ in consumer.ConsumeAsync<RateLimitExemptionsChanged>(cancellationToken: ct))
                {
                    await ReloadAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The sweep still runs, so NATS being down costs promptness, not correctness.
                logger.LogWarning(ex, "Rate-limit exemption listener unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var edge = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
            var cidrs = await edge.RateLimitExemptions.AsNoTracking().Select(x => x.Cidr).ToListAsync(ct);
            var ranges = RateLimitExemptionRules.Parse(cidrs);
            exemptions.Replace(ranges);

            // Logged on change only: the five-minute sweep would otherwise repeat it forever.
            var signature = string.Join(',', ranges.Select(r => r.ToString()).Order(StringComparer.Ordinal));
            if (signature != _loaded)
            {
                _loaded = signature;
                logger.LogInformation("Rate-limit exemptions now {Count} range(s): {Ranges}.", ranges.Length, signature);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Keep the last good list. Dropping it on a database blip would suddenly rate-limit
            // exactly the clients an operator said not to.
            logger.LogWarning(ex, "Could not reload rate-limit exemptions; keeping the current {Count}.", exemptions.Count);
        }
    }
}
