using System.Security.Cryptography;
using System.Text;
using Dcms.Plugins.DynamicApps.Api.Model;

namespace Dcms.Plugins.DynamicApps.Data;

/// <summary>
/// The Postgres indexes behind sortable and filterable fields: one partial expression index per
/// field (two for a sortable one, ascending and descending) on <c>apps.records</c>, limited to its
/// table. The key is the same SQL <see cref="QueryCompiler"/> writes for the field, so the planner
/// can match it: a page sorted by a field reads its first rows from the index instead of sorting
/// every row of the table, and a filter on it is a range scan.
/// </summary>
/// <remarks>
/// The automation worker brings the database in line with every published revision through
/// <c>apps.sync_record_indexes</c>, a function the migration creates with the owner's rights.
/// ponytail: one shared table, so every index here costs every query on it some planning and every
/// insert a predicate check; <see cref="MaxPerApp"/> bounds an app, not the platform. Partitioning
/// apps.records by app is the upgrade when the number of apps makes that show.
/// </remarks>
public static class RecordIndexes
{
    /// <summary>Text-like values are indexed by their first characters: a btree entry is capped at ~2.7 KB.</summary>
    public const int PrefixChars = 200;

    public const int MaxPerApp = 64;

    /// <summary>
    /// All of a tenant's apps together, oldest app first: every index on the shared table costs every
    /// tenant's queries some planning, so one tenant's many apps must not add without bound.
    /// </summary>
    public const int MaxPerTenant = 128;

    /// <summary>Text and Number are btree keys for sorting and filtering; Search is a trigram index for free-text search.</summary>
    public enum KeyKind { Text, Number, Search }

    /// <param name="Name">A stable name from the tenant, app, table, field, kind and direction: <c>ix_da_</c> + 24 hex.</param>
    /// <remarks>
    /// Partial over the tenant, the instance AND the table: authors may choose table ids, so a table id
    /// alone could put another tenant's rows under this app's index (and its cast).
    /// </remarks>
    public sealed record Spec(string Name, Guid TenantId, Guid InstanceId, Guid TableId, string Key, KeyKind Kind, bool Descending);

    /// <summary>What a field of this type is indexed as, or null for types an index does not help.</summary>
    public static KeyKind? KindOf(FieldType type) => type switch
    {
        // Dates are stored in one fixed-width ISO form, so their text orders as they do.
        FieldType.Text or FieldType.LongText or FieldType.Email or FieldType.Url or FieldType.Choice
            or FieldType.Date or FieldType.DateTime => KeyKind.Text,
        FieldType.Integer or FieldType.Decimal => KeyKind.Number,
        _ => null,
    };

    /// <summary>
    /// The key over <paramref name="data"/> (<c>r."Data"</c>) at <paramref name="key"/> (an SQL text
    /// literal or parameter). The index and the compiler both use this, character for character.
    /// </summary>
    public static string KeySql(string data, string key, KeyKind kind) => kind switch
    {
        KeyKind.Number => $"(({data} ->> {key}))::numeric",
        KeyKind.Search => $"lower(({data} ->> {key}))",
        _ => $"left(({data} ->> {key}), {PrefixChars})",
    };

    /// <summary>The field types free-text search reads (QueryCompiler.Search), so the ones a trigram index serves.</summary>
    public static bool Searchable(FieldType type) => type is FieldType.Text or FieldType.LongText or FieldType.Email or FieldType.Url;

    /// <summary>How many indexes a configuration's flags ask for, before <see cref="MaxPerApp"/>.</summary>
    public static int Wanted(AppConfig config) => Fields(config).Sum(f => f.Kind == KeyKind.Search ? 1 : f.Field.Sortable ? 2 : 1);

    /// <summary>
    /// The indexes an app's configuration wants, in table and field order: ascending for a sortable or
    /// filterable field, descending as well for a sortable one, the first <see cref="MaxPerApp"/>.
    /// </summary>
    public static IReadOnlyList<Spec> For(AppConfig config, Guid tenantId, Guid instanceId)
    {
        var all = new List<Spec>();
        foreach (var (table, field, kind) in Fields(config))
        {
            var key = field.Id.ToString();
            all.Add(Make(tenantId, instanceId, table.Id, key, kind, descending: false));
            if (kind != KeyKind.Search && field.Sortable)
            {
                all.Add(Make(tenantId, instanceId, table.Id, key, kind, descending: true));
            }
        }
        return all.Take(MaxPerApp).ToList();
    }

    private static IEnumerable<(TableDef Table, FieldDef Field, KeyKind Kind)> Fields(AppConfig config)
    {
        foreach (var table in config.Tables.Where(t => t.Enabled))
        {
            foreach (var field in table.Fields.Where(f => f.Sortable || f.Filterable))
            {
                if (KindOf(field.Type) is { } kind)
                {
                    yield return (table, field, kind);
                }
            }
            // Search reads the searchable text fields, or the primary field when there are none.
            var searched = table.Fields.Where(f => f.Searchable && Searchable(f.Type)).ToList();
            if (searched.Count == 0 && table.Fields.FirstOrDefault(f => f.Id == table.PrimaryFieldId) is { } primary && Searchable(primary.Type))
            {
                searched.Add(primary);
            }
            foreach (var field in searched)
            {
                yield return (table, field, KeyKind.Search);
            }
        }
    }

    private static Spec Make(Guid tenant, Guid instance, Guid table, string key, KeyKind kind, bool descending)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenant:N}|{instance:N}|{table:N}|{key}|{kind}|{descending}"));
        return new Spec("ix_da_" + Convert.ToHexStringLower(hash)[..24], tenant, instance, table, key, kind, descending);
    }
}
