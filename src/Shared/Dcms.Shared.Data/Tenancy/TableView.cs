namespace Dcms.Shared.Data.Tenancy;

/// <summary>
/// A saved view of a console table (#278): its columns, sort and filter
/// (<see cref="Dcms.Shared.Kernel.Views.ViewDefinition"/>, as JSON). Personal to its owner until
/// shared, read-only, with the whole workspace or with chosen members and roles; only the owner
/// changes it.
/// </summary>
/// <remarks>
/// <see cref="TableKey"/> names the table: <c>content:{instance}:{type}</c>, <c>apps:{instance}:{table}</c>,
/// <c>dataset:{instance}:{set}</c>. Seeing a view never grants the table — that is checked by key.
/// </remarks>
public sealed class TableView : EnvironmentEntity
{
    public string TableKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid OwnerUserId { get; set; }
    public string DefinitionJson { get; set; } = "{}";
    public bool SharedWithWorkspace { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<TableViewShare> Shares { get; set; } = [];
}

/// <summary>A view shared with one member (<see cref="UserId"/>) or one role (<see cref="RoleId"/>).</summary>
public sealed class TableViewShare : EnvironmentEntity
{
    public Guid ViewId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? RoleId { get; set; }
}

/// <summary>
/// The view a member opens a table with. <see cref="ViewKey"/> is a <see cref="TableView"/> id,
/// or a table's own built-in view (<c>system:{id}</c>, a Dynamic Apps maker view).
/// </summary>
public sealed class TableViewDefault : EnvironmentEntity
{
    public Guid UserId { get; set; }
    public string TableKey { get; set; } = string.Empty;
    public string ViewKey { get; set; } = string.Empty;
}
