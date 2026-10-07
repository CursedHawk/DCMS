using System.Text.Json;
using System.Text.Json.Serialization;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Edge;
using Microsoft.EntityFrameworkCore;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.Edge.Auth;

[JsonConverter(typeof(JsonStringEnumConverter<SiteAccess>))]
public enum SiteAccess
{
    [JsonStringEnumMemberName("public")] Public,
    [JsonStringEnumMemberName("signedIn")] SignedIn,
    [JsonStringEnumMemberName("groups")] Groups,
}

/// <summary>One access rule of a tenant site (ADR 0022): paths under <see cref="Prefix"/> need <see cref="Access"/>.</summary>
public sealed record SiteRule(string Prefix, SiteAccess Access, IReadOnlyList<Guid>? Groups = null);

/// <summary>A tenant host the edge signs people in on: whose realm, and its rules in order.</summary>
public sealed record SiteGateEntry(Guid TenantId, string RealmSlug, IReadOnlyList<SiteRule> Rules)
{
    public string ClientId => $"site:{TenantId:N}";
}

public static class SiteGateRules
{
    /// <summary>
    /// The rule deciding a path: the first, in order, whose prefix covers it — <c>/portal</c>
    /// covers <c>/portal</c> and <c>/portal/x</c>, not <c>/portals</c>. Null means public.
    /// Compared without case, as a browser does not care and a rule must not be dodged by
    /// <c>/Portal</c>; and with dot segments and repeated slashes resolved. The path is the one
    /// Kestrel already decoded, and is never decoded again: see <see cref="IsAmbiguous"/>.
    /// </summary>
    public static SiteRule? Decide(IReadOnlyList<SiteRule> rules, string path)
    {
        var normalized = Normalize(path);
        foreach (var rule in rules)
        {
            var prefix = rule.Prefix.TrimEnd('/');
            if (prefix.Length == 0
                || normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return rule.Access == SiteAccess.Public ? null : rule;
            }
        }
        return null;
    }

    /// <summary>
    /// A (Kestrel-decoded) path the gate refuses to judge on a gated host: a <c>%</c> left over
    /// is an encoded slash or a second layer of encoding, and a backslash is a separator to some
    /// servers and a character to others. Either way the edge and the server behind could
    /// disagree about which file it names, which is exactly how a rule is walked around, so
    /// such a request gets a 400 instead of a guess.
    /// </summary>
    public static bool IsAmbiguous(string path) => path.Contains('%') || path.Contains('\\');

    /// <summary>The path with dot segments and repeated slashes resolved. Not decoded: it already is.</summary>
    public static string Normalize(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                continue;
            }
            segments.Add(segment);
        }
        return "/" + string.Join('/', segments);
    }
}

/// <summary>
/// Every gated tenant host's rules (<c>edge.site_gates</c>), in memory because they are consulted
/// on every request to a tenant site.
/// </summary>
public sealed class SiteGates
{
    private volatile IReadOnlyDictionary<string, SiteGateEntry> _hosts = new Dictionary<string, SiteGateEntry>();

    public int Count => _hosts.Count;

    public SiteGateEntry? For(string host) => _hosts.GetValueOrDefault(host.ToLowerInvariant());

    public void Replace(IReadOnlyDictionary<string, SiteGateEntry> hosts) => _hosts = hosts;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// Keeps <see cref="SiteGates"/> current, as <c>RateLimitExemptionLoader</c> keeps its list: a
/// full read at startup, again whenever admin-api announces a change, and every few minutes
/// regardless, so a missed message costs minutes rather than a rule that never arrives.
/// </summary>
public sealed class SiteGateLoader(SiteGates gates, IServiceScopeFactory scopes, INatsJSContext jetStream, ILogger<SiteGateLoader> logger)
    : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(2);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(SweepAsync(stoppingToken), ListenAsync(stoppingToken));

    public async Task ReloadAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var rows = await scope.ServiceProvider.GetRequiredService<EdgeDbContext>().SiteGates.AsNoTracking().ToListAsync(ct);
            var hosts = new Dictionary<string, SiteGateEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                try
                {
                    var rules = JsonSerializer.Deserialize<List<SiteRule>>(row.RulesJson, SiteGates.Json) ?? [];
                    hosts[row.Hostname] = new SiteGateEntry(row.TenantId, row.RealmSlug, rules);
                }
                catch (JsonException ex)
                {
                    // Fail closed for that host: everything behind sign-in, rather than public.
                    logger.LogError(ex, "Unreadable site gate rules for {Host}; requiring sign-in for the whole site.", row.Hostname);
                    hosts[row.Hostname] = new SiteGateEntry(row.TenantId, row.RealmSlug, [new SiteRule("/", SiteAccess.SignedIn)]);
                }
            }
            gates.Replace(hosts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep the rules already loaded: a database blip must not make gated sites public.
            logger.LogWarning(ex, "Could not reload site gates; keeping the {Count} loaded.", gates.Count);
        }
    }

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
                    new NatsJSOrderedConsumerOpts { FilterSubjects = [Subjects.SiteGatesChanged], DeliverPolicy = ConsumerConfigDeliverPolicy.New },
                    ct);
                await foreach (var _ in consumer.ConsumeAsync<SiteGatesChanged>(cancellationToken: ct))
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
                logger.LogWarning(ex, "Site gate listener unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }
}
