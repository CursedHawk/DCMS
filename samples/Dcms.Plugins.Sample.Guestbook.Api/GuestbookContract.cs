using System.Text.Json;
using System.Text.Json.Serialization;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.Sample.Guestbook.Api;

/// <summary>Permission keys the guestbook declares; operations and routes require them.</summary>
public static class GuestbookPermissions
{
    /// <summary>See entries and counts in the admin.</summary>
    public const string Read = "plugin:sample-guestbook:read";

    /// <summary>Approve, reject, edit and delete entries.</summary>
    public const string Moderate = "plugin:sample-guestbook:moderate";

    /// <summary>Export a guestbook's entries as a file.</summary>
    public const string Export = "plugin:sample-guestbook:export";
}

/// <summary>Where an entry stands with the moderators.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EntryStatus>))]
public enum EntryStatus
{
    /// <summary>Waiting for a moderator (pre-moderated guestbooks).</summary>
    Pending,

    /// <summary>Shown on the site.</summary>
    Approved,

    /// <summary>Turned down; kept for the record, never shown.</summary>
    Rejected,
}

/// <summary>One signature in a guestbook.</summary>
/// <param name="Id">Stable, sortable newest-first.</param>
/// <param name="VisitorId">The signed-in site visitor who signed, when there was one.</param>
/// <param name="Reply">A moderator's public answer, shown under the entry.</param>
public sealed record GuestbookEntry(
    string Id, string Name, string Message, EntryStatus Status, DateTimeOffset SignedAt, Guid? VisitorId,
    string? Reply = null);

/// <summary>A page of entries.</summary>
public sealed record EntryList(IReadOnlyList<GuestbookEntry> Entries, long Total);

/// <summary>Which page of approved entries.</summary>
public sealed record ListEntries(int Page = 1, int PageSize = 20);

/// <summary>A visitor's signature.</summary>
public sealed record SignGuestbook(string Name, string Message);

/// <summary>The stored entry, and whether a moderator still has to approve it.</summary>
public sealed record SignResult(GuestbookEntry Entry, bool NeedsApproval);

/// <summary>An entry by id.</summary>
public sealed record EntryRef(string EntryId);

/// <summary>A guestbook's counts, and what the site's blog last published (when it has one).</summary>
public sealed record GuestbookStats(long Pending, long Approved, long Rejected, string? LatestBlogPost);

/// <summary>Raised when a visitor signs. Moderators are told when it needs approval.</summary>
[ContractEvent("guestbook.entry.signed")]
public sealed record EntrySigned(Guid InstanceId, string EntryId, string Name, bool NeedsApproval) : IPluginEvent;

/// <summary>Raised when a moderator approves an entry: it is on the site now.</summary>
[ContractEvent("guestbook.entry.approved")]
public sealed record EntryApproved(Guid InstanceId, string EntryId) : IPluginEvent;

/// <summary>
/// A visitor is signing: runs before the entry is stored. An interceptor may rewrite the name
/// or message (a filter masking words) or cancel with a reason the visitor sees.
/// </summary>
[ContractHook("guestbook.signing")]
public sealed record EntrySigning(Guid InstanceId, string Name, string Message, Guid? VisitorId) : IPluginHook;

/// <summary>
/// A guestbook on the tenant's site: visitors sign it, moderators approve what shows. One
/// contract, three audiences — the site signs and lists, the admin and AI agents moderate and
/// count, and other plugins do any of it in-process.
/// </summary>
[DcmsContract("guestbook.entries", 1,
    Description = "A guestbook visitors sign on the site and moderators approve.",
    Events = [typeof(EntrySigned), typeof(EntryApproved)],
    Hooks = [typeof(EntrySigning)])]
public interface IGuestbook
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai, ReturnsExternalText = true,
        Description = "Approved entries, newest first.")]
    Task<EntryList> ListAsync(ListEntries input, CancellationToken ct);

    [Operation(OpRisk.Safe, Expose = OpExposure.Site,
        Description = "Sign the guestbook. Pre-moderated guestbooks hold the entry until a moderator approves it.")]
    Task<SignResult> SignAsync(SignGuestbook input, CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = GuestbookPermissions.Moderate, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Approve a pending entry so it shows on the site.")]
    Task<GuestbookEntry> ApproveAsync(EntryRef input, CancellationToken ct);

    [Operation(OpRisk.Read, Permission = GuestbookPermissions.Read, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "How many entries wait, show and were turned down.")]
    Task<GuestbookStats> StatsAsync(CancellationToken ct);
}

/// <summary>JSON shape helpers shared by the plugin and anyone reading its stored documents.</summary>
public static class GuestbookJson
{
    /// <summary>The web defaults the plugin stores documents with.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
