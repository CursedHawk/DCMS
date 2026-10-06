using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dcms.Plugins.DynamicApps.Api.Model;

/// <summary>
/// An application's whole configuration: one document per revision (ADR 0021). Every resource
/// has a stable <c>Id</c>, which is its identity, and an <c>ApiName</c>, which is what URLs, the
/// generated client and change sets use. Display names are labels and may change freely.
/// </summary>
public sealed record AppConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public AppSettings Settings { get; init; } = new();
    public IReadOnlyList<TableDef> Tables { get; init; } = [];
    public IReadOnlyList<RelationshipDef> Relationships { get; init; } = [];
    public IReadOnlyList<ChoiceSetDef> ChoiceSets { get; init; } = [];
    public IReadOnlyList<ViewDef> Views { get; init; } = [];
}

public sealed record AppSettings
{
    /// <summary>What the application is for; shown in the admin and the generated API description.</summary>
    public string? Description { get; init; }
}

public sealed record TableDef
{
    public Guid Id { get; init; }

    /// <summary>snake_case, unique in the app; the table's URL segment. Fixed once published.</summary>
    public string ApiName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;
    public string? PluralName { get; init; }
    public string? Description { get; init; }

    /// <summary>The text field that names a record in lists and lookups.</summary>
    public Guid? PrimaryFieldId { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>What the public site may do with this table's records. Nothing, by default.</summary>
    public PublicAccess Public { get; init; } = new();

    public IReadOnlyList<FieldDef> Fields { get; init; } = [];
    public IReadOnlyList<IndexDef> Indexes { get; init; } = [];
}

[JsonConverter(typeof(CamelCaseEnumConverter<FieldType>))]
public enum FieldType
{
    Text,
    LongText,
    Integer,
    Decimal,
    Boolean,
    Date,
    DateTime,
    Email,
    Url,
    Choice,
    MultiChoice,

    /// <summary>A media library asset id, resolved through <c>dcms.media</c>.</summary>
    Media,
    Json,
}

public sealed record FieldDef
{
    public Guid Id { get; init; }

    /// <summary>snake_case, unique among the table's fields and relationships. Fixed once published.</summary>
    public string ApiName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public FieldType Type { get; init; } = FieldType.Text;
    public bool Required { get; init; }
    public bool Unique { get; init; }
    public bool Searchable { get; init; }
    public bool Sortable { get; init; }
    public bool Filterable { get; init; }

    /// <summary>Hidden from new forms and the generated API's writes; existing values are kept.</summary>
    public bool Deprecated { get; init; }

    /// <summary>Only automations write it; every API refuses a value for it.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Never served on the public site, whatever the table's public access.</summary>
    public bool HiddenFromPublic { get; init; }

    /// <summary>A literal of the field's type, used when a new record leaves it out.</summary>
    public JsonNode? Default { get; init; }

    /// <summary>Text types: the longest value accepted.</summary>
    public int? MaxLength { get; init; }

    /// <summary>Number types: inclusive bounds.</summary>
    public decimal? Minimum { get; init; }
    public decimal? Maximum { get; init; }

    /// <summary>Choice types: the set the value comes from.</summary>
    public Guid? ChoiceSetId { get; init; }
}

public sealed record IndexDef
{
    public Guid Id { get; init; }
    public string ApiName { get; init; } = string.Empty;
    public IReadOnlyList<Guid> FieldIds { get; init; } = [];
    public bool Unique { get; init; }
}

[JsonConverter(typeof(CamelCaseEnumConverter<PublicRead>))]
public enum PublicRead
{
    None,

    /// <summary>Every record.</summary>
    All,

    /// <summary>The signed-in site visitor's own records only.</summary>
    Own,
}

/// <summary>
/// The public site's access to one table, served at <c>/api/{slug}/{table}</c>. The admin and
/// the automations are governed by the plugin's permissions instead.
/// </summary>
public sealed record PublicAccess
{
    public PublicRead Read { get; init; } = PublicRead.None;

    /// <summary>Visitors may create records; a signed-in visitor becomes the record's owner.</summary>
    public bool Create { get; init; }

    /// <summary>A signed-in visitor may change their own records.</summary>
    public bool UpdateOwn { get; init; }

    /// <summary>A signed-in visitor may delete their own records.</summary>
    public bool DeleteOwn { get; init; }
}

[JsonConverter(typeof(CamelCaseEnumConverter<RelationshipKind>))]
public enum RelationshipKind
{
    /// <summary>Each source record points at one target (a lookup); the target sees a list. 1:N from the other side.</summary>
    ManyToOne,

    /// <summary>A lookup whose target may be pointed at by one source record only.</summary>
    OneToOne,

    /// <summary>Both sides see a list; links are stored as their own rows.</summary>
    ManyToMany,
}

[JsonConverter(typeof(CamelCaseEnumConverter<DeleteBehavior>))]
public enum DeleteBehavior
{
    /// <summary>Deleting a target that is still referenced fails.</summary>
    Restrict,

    /// <summary>References to a deleted target are cleared.</summary>
    SetNull,

    /// <summary>Referencing records are deleted with their target.</summary>
    Cascade,
}

/// <summary>
/// A link between two tables. A many-to-one or one-to-one relationship is itself the source
/// table's lookup field: records store the target's id under <see cref="ApiName"/>.
/// </summary>
public sealed record RelationshipDef
{
    public Guid Id { get; init; }

    /// <summary>The name on the source table (<c>deal.customer</c>).</summary>
    public string ApiName { get; init; } = string.Empty;

    public string? DisplayName { get; init; }
    public RelationshipKind Kind { get; init; } = RelationshipKind.ManyToOne;
    public Guid SourceTableId { get; init; }
    public Guid TargetTableId { get; init; }

    /// <summary>The name on the target table (<c>customer.deals</c>); required for many-to-many.</summary>
    public string? InverseApiName { get; init; }

    /// <summary>Lookups only: a source record must point at a target.</summary>
    public bool Required { get; init; }

    public DeleteBehavior OnDelete { get; init; } = DeleteBehavior.Restrict;
}

public sealed record ChoiceSetDef
{
    public Guid Id { get; init; }
    public string ApiName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public IReadOnlyList<ChoiceOption> Options { get; init; } = [];
}

public sealed record ChoiceOption
{
    /// <summary>What is stored; fixed once published.</summary>
    public string Value { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;
    public string? Color { get; init; }
}

/// <summary>A saved way of looking at a table: which columns, in which order.</summary>
public sealed record ViewDef
{
    public Guid Id { get; init; }

    /// <summary>Unique within its table.</summary>
    public string ApiName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;
    public Guid TableId { get; init; }

    /// <summary>Field or lookup-relationship ids of the table, in display order.</summary>
    public IReadOnlyList<Guid> Columns { get; init; } = [];

    public IReadOnlyList<ViewSort> Sort { get; init; } = [];

    /// <summary>The table's view when none is chosen.</summary>
    public bool IsDefault { get; init; }
}

public sealed record ViewSort
{
    public Guid FieldId { get; init; }
    public bool Descending { get; init; }
}

/// <summary>Enums on the wire in camelCase ("manyToOne", "longText"), as the configuration documents use.</summary>
public sealed class CamelCaseEnumConverter<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    where T : struct, Enum;
