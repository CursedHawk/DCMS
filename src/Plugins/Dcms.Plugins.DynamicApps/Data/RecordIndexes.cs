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

    public enum KeyKind { Text, Number }

    /// <param name="Name">A stable name from the table, field, kind and direction: <c>ix_da_</c> + 24 hex.</param>
    public sealed record Spec(string Name, Guid TableId, string Key, KeyKind Kind, bool Descending);

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
    public static string KeySql(string data, string key, KeyKind kind) => kind == KeyKind.Number
        ? $"(({data} ->> {key}))::numeric"
        : $"left(({data} ->> {key}), {PrefixChars})";

    /// <summary>
    /// The indexes a configuration wants, in table and field order: ascending for a sortable or
    /// filterable field, descending as well for a sortable one. Past <see cref="MaxPerApp"/> the rest
    /// are left out; <paramref name="wanted"/> says how many there would have been.
    /// </summary>
    public static IReadOnlyList<Spec> For(AppConfig config, out int wanted)
    {
        var all = new List<Spec>();
        foreach (var table in config.Tables.Where(t => t.Enabled))
        {
            foreach (var field in table.Fields.Where(f => f.Sortable || f.Filterable))
            {
                if (KindOf(field.Type) is not { } kind)
                {
                    continue;
                }
                var key = field.Id.ToString();
                all.Add(Make(table.Id, key, kind, descending: false));
                if (field.Sortable)
                {
                    all.Add(Make(table.Id, key, kind, descending: true));
                }
            }
        }
        wanted = all.Count;
        return all.Take(MaxPerApp).ToList();
    }

    private static Spec Make(Guid table, string key, KeyKind kind, bool descending)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{table:N}|{key}|{kind}|{descending}"));
        return new Spec("ix_da_" + Convert.ToHexStringLower(hash)[..24], table, key, kind, descending);
    }
}
