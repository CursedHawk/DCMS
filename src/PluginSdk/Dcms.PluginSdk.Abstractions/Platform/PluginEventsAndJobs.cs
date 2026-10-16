using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.PluginSdk.Abstractions.Platform;

/// <summary>An event by name, with its record as JSON. Prefer <see cref="PluginContextExtensions.PublishAsync{TEvent}"/>.</summary>
public sealed record EventPublish(string Name, JsonElement Payload);

/// <summary>
/// Publishes events under the contracts the calling plugin provides. Subscribers (plugins whose
/// manifest <c>Subscribes</c> to the event and <c>Consumes</c> its contract) receive it in
/// admin-api, once each, as the event's tenant.
/// </summary>
[DcmsContract("dcms.events", 1, Description = "Publish events declared by the calling plugin's contracts.")]
public interface IPluginEvents
{
    /// <exception cref="ContractValidationException">The event is not declared by a contract the caller provides, or the payload does not match it.</exception>
    [Operation(OpRisk.Safe)]
    Task PublishAsync(EventPublish input, CancellationToken ct);
}

/// <param name="InstanceId">Defaults to the instance being served; null outside one for tenant-wide work.</param>
/// <param name="DelaySeconds">Run no sooner than this; at most one day.</param>
/// <param name="DedupeKey">Identifies the occurrence; a second enqueue with the same key within two minutes is dropped.</param>
public sealed record JobEnqueue(
    string Name,
    JsonElement? Payload = null,
    Guid? InstanceId = null,
    int? DelaySeconds = null,
    string? DedupeKey = null);

/// <summary>Enqueues one of the calling plugin's declared <c>Jobs</c>; admin-api runs it.</summary>
[DcmsContract("dcms.jobs", 1, Description = "Enqueue the calling plugin's background jobs.")]
public interface IPluginJobs
{
    [Operation(OpRisk.Safe)]
    Task EnqueueAsync(JobEnqueue input, CancellationToken ct);
}

public static class PluginContextExtensions
{
    /// <summary>Publishes a typed event (<c>[ContractEvent]</c> record) via <c>dcms.events@1</c>.</summary>
    public static Task PublishAsync<TEvent>(this IPluginContext context, TEvent @event, CancellationToken ct)
        where TEvent : IPluginEvent =>
        context.Contracts.Get<IPluginEvents>().PublishAsync(
            // The runtime type, so an event held as IPluginEvent still publishes under its own name.
            new EventPublish(ContractIds.EventName(@event.GetType()), JsonSerializer.SerializeToElement(@event, @event.GetType(), JsonSerializerOptions.Web)),
            ct);
}

/// <param name="TitleKey">i18n key for the title the consoles show, in the plugin's namespace or the console's; defaults to a generic "plugin: job".</param>
/// <param name="TitleParams">Interpolation values for <paramref name="TitleKey"/>.</param>
/// <param name="RequiredPermission">One of the plugin's own permissions (<c>data-import</c>): who besides the starter sees, cancels and retries it.</param>
/// <param name="DedupeKey">Same key, same tenant: the enqueue returns the job already there.</param>
/// <param name="Cancelable">Whether the handler stops when <see cref="IPluginJobRun.Cancellation"/> fires.</param>
public sealed record TrackedJobEnqueue(
    string Name,
    JsonElement? Payload = null,
    Guid? InstanceId = null,
    string? DedupeKey = null,
    string? TitleKey = null,
    IReadOnlyDictionary<string, string>? TitleParams = null,
    string? RequiredPermission = null,
    bool Cancelable = true);

public sealed record JobTicket(Guid JobId);

/// <summary>
/// Work the plugin runs itself, on a queue of its own, shown on the ledger beside everything
/// else (Dynamic Apps' flow runs). Nothing runs it from the ledger; the plugin reports how it goes.
/// </summary>
/// <param name="Name">What kind of work it is (<c>flow-run</c>): letters, digits and dashes.</param>
/// <param name="Id">The plugin's own id for it, so a repeat of the same work is the same job.</param>
/// <param name="Routine">
/// Work nobody is waiting on, such as a schedule's tick: listed like any job, but a success is
/// left out of the "recently finished" people glance at.
/// </param>
public sealed record TrackedJobMirror(
    string Name,
    Guid Id,
    string? TitleKey = null,
    IReadOnlyDictionary<string, string>? TitleParams = null,
    string? RequiredPermission = null,
    string? LinkPath = null,
    bool Routine = false);

/// <param name="Status">Null for progress alone; otherwise Running, Succeeded, Failed or Canceled.</param>
public sealed record TrackedJobReport(
    Guid JobId,
    string? Status = null,
    long? Done = null,
    long? Total = null,
    string? StageKey = null,
    string? ErrorKey = null,
    string? ErrorDetail = null);

/// <summary>
/// Enqueues one of the calling plugin's declared <c>Jobs</c> as a tracked background job (ADR
/// 0024): it appears in the workspace's jobs ring and page with its progress, can be canceled and
/// retried, and jobs-worker runs it. The returned id is what a screen follows (<c>useJob</c>).
/// <c>dcms.jobs@1</c> remains for fire-and-forget work, delayed work and interval jobs.
/// </summary>
[DcmsContract("dcms.jobs", 2, Description = "Enqueue the calling plugin's background jobs as tracked jobs with progress.")]
public interface IPluginTrackedJobs
{
    [Operation(OpRisk.Safe)]
    Task<JobTicket> EnqueueAsync(TrackedJobEnqueue input, CancellationToken ct);

    /// <summary>Shows work the plugin runs itself on the ledger; the same id twice is the same job.</summary>
    [Operation(OpRisk.Safe)]
    Task<JobTicket> TrackAsync(TrackedJobMirror input, CancellationToken ct);

    /// <summary>Progress or an outcome for a job from <see cref="TrackAsync"/>. A job it did not track is ignored.</summary>
    [Operation(OpRisk.Safe)]
    Task ReportAsync(TrackedJobReport input, CancellationToken ct);
}

/// <summary>The tracked job a handler is running: its progress and its cancellation.</summary>
public interface IPluginJobRun
{
    Guid JobId { get; }

    /// <summary>Fires when someone cancels the job or the worker is stopping.</summary>
    CancellationToken Cancellation { get; }

    /// <summary>Overall progress; cheap to call per item (written at most once a second, never backwards).</summary>
    ValueTask ReportAsync(long done, long? total = null, string? stageKey = null);

    /// <summary>What the job produced, stored on its row as JSON (an import's counts and errors).</summary>
    object? Result { get; set; }
}

/// <summary>
/// The tracked job the current handler is running, or null for a <c>dcms.jobs@1</c> job (which
/// has no progress to report). Set by the runtime around the handler's call.
/// </summary>
public static class PluginJobRun
{
    private static readonly AsyncLocal<IPluginJobRun?> Ambient = new();

    public static IPluginJobRun? Current
    {
        get => Ambient.Value;
        set => Ambient.Value = value;
    }
}
