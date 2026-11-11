using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.Forms.Api;

public static class FormsPermissions
{
    /// <summary>Reading submissions is a content permission: whoever may read the site's content may read what visitors sent.</summary>
    public const string SubmissionsRead = "content:read";

    /// <summary>Marking and deleting them is the matching content write, as in the console's inbox.</summary>
    public const string SubmissionsWrite = "content:write";
}

public sealed record SubmissionSummary(
    Guid Id,
    Guid InstanceId,
    string FormName,
    IReadOnlyDictionary<string, JsonElement> Data,
    Guid? VisitorId,
    DateTimeOffset SubmittedAt,
    bool Handled);

/// <param name="FormName">Limit to one form; all forms of the instance otherwise.</param>
public sealed record SubmissionQuery(string? FormName = null, int Page = 1, int PageSize = 20);

public sealed record SubmissionPage(IReadOnlyList<SubmissionSummary> Items, int Page, int PageSize, long TotalCount);

public sealed record SubmissionRef(Guid SubmissionId);

public sealed record SubmissionHandled(Guid SubmissionId, bool Handled = true);

/// <summary>A form was submitted and stored. The payload is fetched through the contract, not carried here.</summary>
[ContractEvent("form.submitted")]
public sealed record FormSubmitted(Guid SubmissionId, Guid InstanceId, string FormName, Guid? VisitorId) : IPluginEvent;

/// <summary>Read access to a Forms instance's stored submissions.</summary>
[DcmsContract("forms.submissions", 1,
    Description = "Stored submissions of a Forms instance's forms.",
    Events = [typeof(FormSubmitted)],
    Hooks = [typeof(FormSubmitting)])]
public interface IFormSubmissions
{
    [Operation(OpRisk.Read, Permission = FormsPermissions.SubmissionsRead, Expose = OpExposure.Admin | OpExposure.Ai | OpExposure.Link,
        ReturnsExternalText = true, Description = "Newest-first page of submissions, optionally for one form.")]
    Task<SubmissionPage> ListAsync(SubmissionQuery input, CancellationToken ct);

    [Operation(OpRisk.Read, Permission = FormsPermissions.SubmissionsRead, Expose = OpExposure.Admin | OpExposure.Ai | OpExposure.Link,
        ReturnsExternalText = true, Description = "One submission by id.")]
    Task<SubmissionSummary?> GetAsync(SubmissionRef input, CancellationToken ct);
}

/// <summary>
/// A visitor is submitting a form: run before it is stored. Interceptors may rewrite
/// <see cref="Data"/> (only the form's declared fields are kept) or cancel — a spam filter, a
/// blocklist, a CRM enriching the entry. A cancel answers the visitor 400 with the reason.
/// </summary>
[ContractHook("forms.submitting")]
public sealed record FormSubmitting(
    Guid InstanceId, string FormName, IReadOnlyDictionary<string, JsonElement> Data, Guid? VisitorId) : IPluginHook;

/// <summary>
/// The console's inbox actions on a Forms instance's submissions, for the assistant. A contract of
/// its own, not more of <see cref="IFormSubmissions"/>: functions that read submissions vouch for
/// that contract's permissions, and reading must not start to need — or grant — deleting.
/// </summary>
[DcmsContract("forms.inbox", 1, Description = "Mark a Forms instance's submissions handled, or delete them.")]
public interface IFormInbox
{
    [Operation(OpRisk.Safe, Permission = FormsPermissions.SubmissionsWrite, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Mark a submission handled (or not handled again), as the inbox does.")]
    Task MarkHandledAsync(SubmissionHandled input, CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = FormsPermissions.SubmissionsWrite, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Delete a submission for good.")]
    Task DeleteAsync(SubmissionRef input, CancellationToken ct);
}
