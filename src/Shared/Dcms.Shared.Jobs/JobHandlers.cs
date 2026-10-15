using System.Text.Json;

namespace Dcms.Shared.Jobs;

/// <summary>
/// One registered kind: how to turn the row's payload into a typed call on its handler. Built
/// as a closure at registration, so running a job needs no reflection.
/// </summary>
/// <param name="IsPrefix">Handles every kind starting with <see cref="Kind"/> (plugin jobs: <c>plugin:</c>).</param>
public sealed record JobHandlerRegistration(
    string Kind,
    Func<IServiceProvider, string?, IJobContext, Task<object?>> Invoke,
    bool IsPrefix = false);

public sealed class JobHandlerRegistry(IEnumerable<JobHandlerRegistration> registrations)
{
    private readonly Dictionary<string, JobHandlerRegistration> _byKind =
        registrations.Where(r => !r.IsPrefix).ToDictionary(r => r.Kind, StringComparer.Ordinal);

    private readonly List<JobHandlerRegistration> _prefixes =
        [.. registrations.Where(r => r.IsPrefix).OrderByDescending(r => r.Kind.Length)];

    /// <summary>The exact kind's handler, else the longest prefix that covers it.</summary>
    public JobHandlerRegistration? Find(string kind) =>
        _byKind.GetValueOrDefault(kind)
        ?? _prefixes.FirstOrDefault(p => kind.StartsWith(p.Kind, StringComparison.Ordinal));

    internal static Task<object?> Invoke<THandler, TPayload>(IServiceProvider services, string? payloadJson, IJobContext context)
        where THandler : IJobHandler<TPayload>
    {
        var payload = payloadJson is null
            ? default!
            : JsonSerializer.Deserialize<TPayload>(payloadJson, JsonSerializerOptions.Web)!;
        var handler = (THandler)services.GetService(typeof(THandler))!;
        return handler.RunAsync(payload, context);
    }
}

/// <summary>
/// <see cref="IJobContext"/> for a job a worker claimed from the ledger.
///
/// <para>Progress is coalesced: a report inside a second of the last write is held, and the next
/// write (or the heartbeat) carries the newest values. Handlers can therefore report per row of a
/// hundred-thousand-row import without writing a hundred thousand updates.</para>
///
/// <para>The ledger is shared with the heartbeat, which runs beside the handler; a DbContext is not
/// safe for concurrent use, so every ledger call here goes through one gate.</para>
/// </summary>
internal sealed class LedgerJobContext(
    JobLedger ledger,
    Guid jobId,
    Guid? tenantId,
    Guid? parentId,
    int attempt,
    CancellationToken cancellation) : IJobContext
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _sync = new();
    private (long Done, long? Total, string? Stage)? _pending;
    private DateTimeOffset _lastWrite = DateTimeOffset.MinValue;

    public Guid JobId => jobId;
    public Guid? TenantId => tenantId;
    public Guid? ParentId => parentId;
    public int Attempt => attempt;
    public CancellationToken Cancellation => cancellation;

    public async ValueTask ReportAsync(long done, long? total = null, string? stageKey = null)
    {
        lock (_sync)
        {
            var previous = _pending;
            _pending = (done, total ?? previous?.Total, stageKey ?? previous?.Stage);
            if (DateTimeOffset.UtcNow - _lastWrite < MinInterval)
            {
                return;
            }
        }
        await FlushAsync(CancellationToken.None);
    }

    /// <summary>Writes the held progress, if any.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        (long Done, long? Total, string? Stage) report;
        lock (_sync)
        {
            if (_pending is not { } pending)
            {
                return;
            }
            report = pending;
            _pending = null;
            _lastWrite = DateTimeOffset.UtcNow;
        }
        await WithLedgerAsync(l => l.ProgressAsync(jobId, tenantId, report.Done, report.Total, report.Stage, ct), ct);
    }

    /// <summary>Runs one ledger call, never two at once.</summary>
    public async Task<T> WithLedgerAsync<T>(Func<JobLedger, Task<T>> call, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await call(ledger);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task WithLedgerAsync(Func<JobLedger, Task> call, CancellationToken ct) =>
        WithLedgerAsync(async l => { await call(l); return true; }, ct);
}
