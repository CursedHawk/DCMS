using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Data.Jobs;
using Dcms.Shared.Jobs;
using Dcms.Shared.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Runtime.Hosting;

/// <summary>What a tracked plugin job carries on the ledger: whose job, and its own input.</summary>
public sealed record PluginJobPayload(string PluginId, string JobName, Guid? InstanceId, string? PayloadJson);

/// <summary>
/// <see cref="IPluginTrackedJobs"/> (<c>dcms.jobs@2</c>): a plugin's declared job as a job on the
/// ledger (ADR 0024), kind <c>plugin:{plugin}:{job}</c>, run by jobs-worker through
/// <see cref="PluginJobHandler"/>. The same rules as <c>dcms.jobs@1</c> about what may be enqueued.
/// </summary>
public sealed partial class PluginTrackedJobs(IPluginContext caller, PluginRegistry registry, IJobQueue jobs, JobLedger ledger)
    : IPluginTrackedJobs
{
    public const string KindPrefix = "plugin:";

    /// <summary>The lane of work a plugin runs itself: tracked, never dispatched.</summary>
    public const string MirrorLane = "plugin";

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9-]{1,64}$")]
    private static partial System.Text.RegularExpressions.Regex NamePattern();

    public async Task<JobTicket> TrackAsync(TrackedJobMirror input, CancellationToken ct)
    {
        if (!NamePattern().IsMatch(input.Name))
        {
            throw new ContractValidationException("A tracked job's name is 1-64 lowercase letters, digits and dashes.");
        }
        var job = await jobs.EnqueueAsync(new JobSpec(
            $"{KindPrefix}{caller.PluginId}:{input.Name}",
            caller.TenantId,
            null,
            input.TitleKey ?? "jobs.kinds.plugin.title",
            input.TitleParams ?? (object)new { plugin = caller.PluginId, job = input.Name },
            RequiredPermission: input.RequiredPermission is { } permission ? PlatformPermissions.ForPlugin(caller.PluginId, permission) : null,
            Id: input.Id,
            DedupeKey: $"{KindPrefix}{caller.PluginId}:{input.Name}:{input.Id}",
            Cancelable: false,
            Lane: MirrorLane,
            LinkPath: input.LinkPath is { } path ? AdminPath(path) : null,
            Routine: input.Routine,
            Links: Links(input.Links)), ct);
        return new JobTicket(job.Id);
    }

    public async Task ReportAsync(TrackedJobReport input, CancellationToken ct)
    {
        // Only the caller's own tracked work: in its tenant (the ledger checks that), and its kind.
        var job = await ledger.FindAsync(input.JobId, caller.TenantId, ct);
        if (job is null || job.Lane != MirrorLane || !job.Kind.StartsWith($"{KindPrefix}{caller.PluginId}:", StringComparison.Ordinal))
        {
            return;
        }
        await ledger.ApplyReportAsync(new Dcms.Shared.Contracts.Events.JobStateReported(
            Guid.NewGuid(), DateTimeOffset.UtcNow, input.JobId, caller.TenantId, input.Status, input.Done, input.Total,
            input.StageKey, input.ErrorKey, input.ErrorDetail, null), ct);
    }

    public async Task<JobTicket> EnqueueAsync(TrackedJobEnqueue input, CancellationToken ct)
    {
        if (registry.Find(caller.PluginId)?.Jobs?.Any(j => j.Name == input.Name) != true)
        {
            throw new ContractValidationException($"Plugin '{caller.PluginId}' declares no job '{input.Name}'.");
        }
        if (input.InstanceId is { } requested && caller.Instance is { } own && requested != own.InstanceId)
        {
            throw new ContractValidationException("A job can only be enqueued for the instance being served.");
        }
        if (input.DedupeKey is { Length: > 200 })
        {
            throw new ContractValidationException("DedupeKey is at most 200 characters.");
        }
        var payload = input.Payload?.GetRawText();
        if (payload is { Length: > 64 * 1024 })
        {
            throw new ContractValidationException("Job payload is at most 64 KiB; store bulk data with dcms.blobs and pass its key.");
        }

        var kind = $"{KindPrefix}{caller.PluginId}:{input.Name}";
        var job = await jobs.EnqueueAsync(new JobSpec(
            kind,
            caller.TenantId,
            new PluginJobPayload(caller.PluginId, input.Name, input.InstanceId ?? caller.Instance?.InstanceId, payload),
            input.TitleKey ?? "jobs.kinds.plugin.title",
            input.TitleParams ?? (object)new { plugin = caller.PluginId, job = input.Name },
            RequiredPermission: input.RequiredPermission is { } permission
                ? PlatformPermissions.ForPlugin(caller.PluginId, permission)
                : null,
            DedupeKey: input.DedupeKey is { } key ? $"{kind}:{key}" : null,
            Cancelable: input.Cancelable,
            Links: Links(input.Links)), ct);
        return new JobTicket(job.Id);
    }

    /// <summary>
    /// The plugin's links, checked, with the instance's page as the source unless it named one:
    /// every plugin job can say which app it belongs to without the plugin having to.
    /// </summary>
    private List<JobLink> Links(IReadOnlyList<TrackedJobLink>? given)
    {
        if (given is { Count: > 8 })
        {
            throw new ContractValidationException("A tracked job has at most 8 links.");
        }
        var links = (given ?? []).Select(l =>
        {
            if (!JobLinkRels.All.Contains(l.Rel))
            {
                throw new ContractValidationException($"A job link's rel is one of {string.Join(", ", JobLinkRels.All)}.");
            }
            if (l.Name is { Length: > 200 })
            {
                throw new ContractValidationException("A job link's name is at most 200 characters.");
            }
            return new JobLink(l.Rel, AdminPath(l.Path), l.Name);
        }).ToList();
        if (caller.Instance is { } instance && links.All(l => l.Rel != JobLinkRels.Source))
        {
            links.Insert(0, new JobLink(JobLinkRels.Source, $"/apps/{instance.Slug}", instance.Name));
        }
        return links;
    }

    private static string AdminPath(string path) => JobLinkRels.IsAdminPath(path)
        ? path
        : throw new ContractValidationException("A job link must be an admin path starting with a single '/'.");
}

/// <summary>
/// Runs every tracked plugin job (<c>plugin:*</c>): the plugin's own declared handler, inside its
/// tenant and instance like any plugin job (<see cref="PluginHandlerRunner"/>), with
/// <see cref="PluginJobRun.Current"/> giving it the job's progress, cancellation and result.
/// </summary>
public sealed class PluginJobHandler(PluginRegistry registry, PluginHandlerRunner runner) : IJobHandler<PluginJobPayload>
{
    public async Task<object?> RunAsync(PluginJobPayload payload, IJobContext context)
    {
        var declaration = registry.Find(payload.PluginId)?.Jobs?.FirstOrDefault(j => j.Name == payload.JobName)
                          ?? throw new JobFailedException("jobs.error.unknownKind",
                              $"No plugin job '{payload.PluginId}/{payload.JobName}' in this build.");
        var run = new Run(context);
        var ran = await runner.RunAsync(context.TenantId!.Value, payload.PluginId, payload.InstanceId, async (pluginContext, sp) =>
        {
            PluginJobRun.Current = run;
            try
            {
                JsonElement? input = payload.PayloadJson is null ? null : JsonDocument.Parse(payload.PayloadJson).RootElement.Clone();
                var handler = (IPluginJobHandler)ActivatorUtilities.CreateInstance(sp, declaration.Handler);
                await handler.RunAsync(input, pluginContext, context.Cancellation);
            }
            // The plugin's own refusals are answers, not accidents: retrying asks the same question.
            catch (ContractValidationException e)
            {
                throw new JobFailedException("jobs.error.refused", e.Message, e);
            }
            catch (ContractConflictException e)
            {
                throw new JobFailedException("jobs.error.conflict", e.Message, e);
            }
            finally
            {
                PluginJobRun.Current = null;
            }
        }, context.Cancellation);
        if (!ran)
        {
            throw new JobFailedException("jobs.error.pluginDisabled", "The plugin is no longer enabled for this workspace.");
        }
        return run.Result;
    }

    private sealed class Run(IJobContext context) : IPluginJobRun
    {
        public Guid JobId => context.JobId;
        public CancellationToken Cancellation => context.Cancellation;
        public ValueTask ReportAsync(long done, long? total = null, string? stageKey = null) =>
            context.ReportAsync(done, total, stageKey);
        public object? Result { get; set; }
    }
}
