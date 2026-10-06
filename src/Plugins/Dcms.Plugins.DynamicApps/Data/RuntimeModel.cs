using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Metadata;
using Microsoft.Extensions.Caching.Memory;

namespace Dcms.Plugins.DynamicApps.Data;

/// <summary>
/// The published configuration compiled for the data plane: tables, their members and
/// navigations by api name and by id. Built once per published revision — revisions never
/// change, so a cache entry never goes stale; a publish simply makes a new one.
/// </summary>
public sealed class RuntimeModel
{
    private RuntimeModel(RevisionInfo revision, AppConfig config)
    {
        Revision = revision;
        Config = config;
        var tables = config.Tables.Where(t => t.Enabled).Select(t => new RuntimeTable(t, config)).ToList();
        ById = tables.ToDictionary(t => t.Id);
        ByName = tables.ToDictionary(t => t.ApiName, StringComparer.Ordinal);
    }

    public RevisionInfo Revision { get; }
    public AppConfig Config { get; }
    public IReadOnlyDictionary<Guid, RuntimeTable> ById { get; }
    public IReadOnlyDictionary<string, RuntimeTable> ByName { get; }

    public RuntimeTable? Table(string apiName) => ByName.GetValueOrDefault(apiName);

    public static RuntimeModel Compile(RevisionDocument published) => new(published.Revision, published.Config);
}

public enum NavigationKind
{
    /// <summary>A lookup, followed forward: one target record.</summary>
    Lookup,

    /// <summary>A lookup, followed backwards: the source records that point here.</summary>
    Inverse,

    /// <summary>A many-to-many relationship, from either side.</summary>
    ManyToMany,
}

/// <summary>A field, or a lookup (many-to-one, one-to-one) stored on this table's records.</summary>
/// <param name="Options">A choice field's allowed values, from its choice set.</param>
public sealed record RuntimeMember(Guid Id, string ApiName, FieldDef? Field, RelationshipDef? Lookup, IReadOnlyList<string>? Options = null)
{
    /// <summary>The key the value is stored under in a record's data.</summary>
    public string Key { get; } = Id.ToString();

    public bool IsLookup => Lookup is not null;
    public bool Required => Field?.Required ?? Lookup!.Required;
}

/// <summary>A related set reachable from a record: <c>/{table}/{id}/{navigation}</c>.</summary>
/// <param name="FromSource">True when this table is the relationship's source side.</param>
public sealed record RuntimeNavigation(string ApiName, RelationshipDef Relationship, NavigationKind Kind, bool FromSource)
{
    public Guid OtherTableId => FromSource ? Relationship.TargetTableId : Relationship.SourceTableId;
}

/// <summary>A uniqueness rule over a table's records: a unique field, a unique index, or a one-to-one lookup.</summary>
public sealed record UniqueConstraint(Guid Id, IReadOnlyList<RuntimeMember> Members)
{
    /// <summary>What the rule covers; when it changes, the claimed keys have to be rebuilt.</summary>
    public string Signature => string.Join(",", Members.Select(m => $"{m.Id}:{m.Field?.Type.ToString() ?? "lookup"}"));
}

public sealed class RuntimeTable
{
    public RuntimeTable(TableDef table, AppConfig config)
    {
        Def = table;
        var lookups = config.Relationships
            .Where(r => r.SourceTableId == table.Id && r.Kind != RelationshipKind.ManyToMany)
            .Select(r => new RuntimeMember(r.Id, r.ApiName, null, r));
        var choices = config.ChoiceSets.ToDictionary(c => c.Id, c => (IReadOnlyList<string>)c.Options.Select(o => o.Value).ToList());
        Members = table.Fields
            .Select(f => new RuntimeMember(f.Id, f.ApiName, f, null, f.ChoiceSetId is { } c ? choices.GetValueOrDefault(c) : null))
            .Concat(lookups).ToList();
        ByName = Members.ToDictionary(m => m.ApiName, StringComparer.Ordinal);
        ById = Members.ToDictionary(m => m.Id);
        Primary = table.PrimaryFieldId is { } p ? ById.GetValueOrDefault(p) : null;

        var navigations = new List<RuntimeNavigation>();
        foreach (var r in config.Relationships)
        {
            if (r.SourceTableId == table.Id)
            {
                navigations.Add(new RuntimeNavigation(r.ApiName, r,
                    r.Kind == RelationshipKind.ManyToMany ? NavigationKind.ManyToMany : NavigationKind.Lookup, FromSource: true));
            }
            if (r.TargetTableId == table.Id && r.InverseApiName is { } inverse)
            {
                navigations.Add(new RuntimeNavigation(inverse, r,
                    r.Kind == RelationshipKind.ManyToMany ? NavigationKind.ManyToMany : NavigationKind.Inverse, FromSource: false));
            }
        }
        Navigations = navigations.DistinctBy(n => n.ApiName).ToDictionary(n => n.ApiName, StringComparer.Ordinal);

        Uniques = table.Fields.Where(f => f.Unique).Select(f => new UniqueConstraint(f.Id, [ById[f.Id]]))
            .Concat(table.Indexes.Where(i => i.Unique)
                .Select(i => new UniqueConstraint(i.Id, i.FieldIds.Where(ById.ContainsKey).Select(id => ById[id]).ToList())))
            .Concat(Members.Where(m => m.Lookup?.Kind == RelationshipKind.OneToOne).Select(m => new UniqueConstraint(m.Id, [m])))
            .Where(u => u.Members.Count > 0)
            .ToList();
    }

    public TableDef Def { get; }
    public Guid Id => Def.Id;
    public string ApiName => Def.ApiName;
    public IReadOnlyList<RuntimeMember> Members { get; }
    public IReadOnlyDictionary<string, RuntimeMember> ByName { get; }
    public IReadOnlyDictionary<Guid, RuntimeMember> ById { get; }
    public IReadOnlyDictionary<string, RuntimeNavigation> Navigations { get; }
    public IReadOnlyList<UniqueConstraint> Uniques { get; }
    public RuntimeMember? Primary { get; }
}

/// <summary>The published model of the instance being served, compiled once per revision.</summary>
public sealed class RuntimeModelProvider(ConfigurationService configuration, IMemoryCache cache)
{
    /// <summary>Null when nothing has been published: the data plane has no tables yet.</summary>
    public async Task<RuntimeModel?> GetAsync(CancellationToken ct)
    {
        if (await configuration.GetPublishedAsync(ct) is not { } published)
        {
            return null;
        }
        return await cache.GetOrCreateAsync(("dynamic-apps:model", published.Revision.Id), entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromMinutes(30);
            return Task.FromResult(RuntimeModel.Compile(published));
        }) ?? RuntimeModel.Compile(published);
    }
}
