using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dcms.Plugins.DynamicApps.Api.Model;

/// <summary>
/// One edit in a change set. Resources are addressed by id or by api name:
/// <c>deals</c> (table, choice set), <c>deals.amount</c> (field, index, view, or a relationship
/// by its source table). Inside <see cref="Value"/>, every <c>…Id</c> reference
/// (<c>tableId</c>, <c>sourceTableId</c>, <c>choiceSetId</c>, <c>fieldIds</c>, <c>columns</c>…)
/// may also be an api name, so one change set can create a table and refer to it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>create</c>: <see cref="Value"/> is the new resource; an id is assigned when absent.
/// Fields and indexes are created with <see cref="Target"/> naming their table; a table may be
/// created with its fields inline.</item>
/// <item><c>update</c>: <see cref="Value"/> is a JSON merge patch of the resource. A table's
/// fields and indexes change through their own operations.</item>
/// <item><c>delete</c>: removes the resource and whatever cannot exist without it (a table's
/// fields, views and relationships; a field's index entries and view columns).</item>
/// </list>
/// </remarks>
public sealed record ChangeOperation
{
    /// <summary><c>create</c>, <c>update</c> or <c>delete</c>.</summary>
    public string Op { get; init; } = string.Empty;

    /// <summary><c>settings</c>, <c>table</c>, <c>field</c>, <c>index</c>, <c>relationship</c>, <c>choiceSet</c> or <c>view</c>.</summary>
    public string Type { get; init; } = string.Empty;

    public string? Target { get; init; }
    public JsonObject? Value { get; init; }
}

/// <summary>One logical difference between two configurations: <c>+ field deals.amount</c>.</summary>
public sealed record ConfigChange
{
    /// <summary><c>create</c>, <c>update</c> or <c>delete</c>.</summary>
    public string Op { get; init; } = string.Empty;

    public string ResourceType { get; init; } = string.Empty;
    public Guid? ResourceId { get; init; }

    /// <summary>Api-name address, e.g. <c>deals.amount</c>.</summary>
    public string Path { get; init; } = string.Empty;

    public JsonNode? Before { get; init; }
    public JsonNode? After { get; init; }

    /// <summary>Publishing it can lose or reject existing data: a deletion, a type change, a new constraint.</summary>
    public bool Destructive { get; init; }
}

[JsonConverter(typeof(CamelCaseEnumConverter<IssueSeverity>))]
public enum IssueSeverity
{
    Error,
    Warning,
}

/// <summary>Why a configuration cannot be published (an error) or might surprise (a warning).</summary>
public sealed record ConfigIssue(IssueSeverity Severity, string Code, string Path, string Message);

/// <summary>What is known about a revision without its document.</summary>
public sealed record RevisionInfo
{
    public Guid Id { get; init; }
    public int Number { get; init; }

    /// <summary><c>draft</c>, <c>published</c>, <c>superseded</c>, <c>rolledBack</c> or <c>discarded</c>.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary><c>human</c>, <c>ai</c>, <c>system</c> or <c>import</c>.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Send this as <c>expectedHash</c> with the next change to a draft.</summary>
    public string Hash { get; init; } = string.Empty;

    public Guid? ParentId { get; init; }
    public Guid? BasePublishedId { get; init; }
    public string? Description { get; init; }
    public string? CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Set when the revision's current content has passed validation.</summary>
    public DateTimeOffset? ValidatedAt { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }
    public string? PublishedBy { get; init; }
    public Guid? SourceConversationId { get; init; }
    public Guid? SourceAiRunId { get; init; }
}

/// <param name="Hash">
/// What to send as <c>expectedHash</c> with the next change: the draft's hash, else the
/// published revision's (a change then opens a draft from it), else the empty configuration's.
/// </param>
public sealed record AppState(RevisionInfo? Draft, RevisionInfo? Published, string Hash);

public sealed record RevisionDocument(RevisionInfo Revision, AppConfig Config);

public sealed record ApplyChangesRequest(string ExpectedHash, IReadOnlyList<ChangeOperation> Operations, string? Description = null);

/// <param name="Changes">What this change set did, as recorded in the revision's change log.</param>
/// <param name="Issues">Validation of the whole draft after the change: a draft may be invalid, a publish may not.</param>
public sealed record ApplyChangesResult(RevisionInfo Draft, IReadOnlyList<ConfigChange> Changes, IReadOnlyList<ConfigIssue> Issues);

public sealed record ValidationResult(RevisionInfo Revision, IReadOnlyList<ConfigIssue> Issues)
{
    public bool Valid => Issues.All(i => i.Severity != IssueSeverity.Error);
}

/// <summary>What publishing the draft would change, compared with what is live now.</summary>
public sealed record RevisionPreview(
    RevisionInfo Draft,
    RevisionInfo? Published,
    IReadOnlyList<ConfigChange> Changes,
    IReadOnlyList<ConfigIssue> Issues)
{
    public bool CanPublish => Issues.All(i => i.Severity != IssueSeverity.Error) && Draft.BasePublishedId == Published?.Id;
    public bool HasDestructiveChanges => Changes.Any(c => c.Destructive);
}

public sealed record ExpectedHashRequest(string ExpectedHash);

public sealed record CreateDraftRequest(string? Description = null);

/// <param name="Published">False when validation refused it; <see cref="Issues"/> says why.</param>
public sealed record PublishResult(bool Published, RevisionInfo? Revision, IReadOnlyList<ConfigIssue> Issues);

/// <param name="ExpectedPublishedHash">Optional guard: the live revision the caller saw.</param>
public sealed record RollbackRequest(int ToRevision, string? ExpectedPublishedHash = null, string? Description = null);

public sealed record RevisionPage(IReadOnlyList<RevisionInfo> Items, int Total);

public sealed record RevisionDiff(RevisionInfo From, RevisionInfo To, IReadOnlyList<ConfigChange> Changes);
