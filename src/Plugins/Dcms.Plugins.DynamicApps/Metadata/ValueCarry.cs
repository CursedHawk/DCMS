using Dcms.Plugins.DynamicApps.Api.Model;

namespace Dcms.Plugins.DynamicApps.Metadata;

/// <summary>
/// Moving existing values when the model changes, so a better model does not cost the data:
/// a field converted in place (text → choice, choice → multi-choice), or a new field or
/// relationship that names, as <c>copyFrom</c>, the live field it replaces (a <c>company_id</c>
/// text field becoming the <c>company</c> lookup). Which values qualify is decided here, once,
/// for the validator and for the publish that moves them; values that do not are left behind
/// and reported, never coerced.
/// </summary>
public static class ValueCarry
{
    private static readonly FieldType[] TextLike = [FieldType.Text, FieldType.LongText];

    /// <summary>In-place type changes that keep the field (and its values) when every value fits.</summary>
    public static bool IsConversion(FieldType from, FieldType to) =>
        (TextLike.Contains(from) && to == FieldType.Choice) || (from == FieldType.Choice && to == FieldType.MultiChoice);

    /// <summary>Why <paramref name="source"/>'s values cannot feed the new member; null when they can.</summary>
    /// <param name="targetType">The new field's type; null for a lookup.</param>
    public static string? Incompatible(FieldDef source, FieldType? targetType) => targetType switch
    {
        null when TextLike.Contains(source.Type) => null,
        null => $"a lookup can only take its values from a text field holding record ids, not a {Camel(source.Type)} field",
        { } t when t == source.Type => null,
        { } t when IsConversion(source.Type, t) => null,
        { } t when TextLike.Contains(source.Type) && TextLike.Contains(t) => null,
        { } t => $"a {Camel(t)} field cannot take the values of a {Camel(source.Type)} field",
    };

    /// <summary>
    /// SQL over <c>apps.records r</c> (positional parameters from <paramref name="first"/>): the
    /// condition a record's value under <paramref name="from"/> must meet to move, and the jsonb
    /// it becomes. Parameters, in order: source key, then options (choice targets) or target
    /// table id (lookups).
    /// </summary>
    public static (string Condition, string Value, object[] Parameters) Sql(
        FieldType sourceType, FieldType? targetType, string from, IReadOnlyList<string> options, Guid? targetTable, int first)
    {
        var key = $"{{{first}}}";
        var extra = $"{{{first + 1}}}";
        return targetType switch
        {
            // A record id written as text: kept only when that record exists.
            null => (
                $"""EXISTS (SELECT 1 FROM apps.records t WHERE t."InstanceId" = r."InstanceId" AND t."TableId" = {extra} AND t."Id"::text = lower(btrim(r."Data" ->> {key})))""",
                $"""to_jsonb(lower(btrim(r."Data" ->> {key})))""",
                [from, targetTable!.Value]),
            FieldType.Choice => (
                $"""(r."Data" ->> {key}) = ANY({extra})""",
                $"""r."Data" -> {key}""",
                [from, options.ToArray()]),
            FieldType.MultiChoice when sourceType == FieldType.Choice => (
                $"""(r."Data" ->> {key}) = ANY({extra})""",
                $"""jsonb_build_array(r."Data" ->> {key})""",
                [from, options.ToArray()]),
            _ => (
                $"""jsonb_typeof(r."Data" -> {key}) <> 'null'""",
                $"""r."Data" -> {key}""",
                [from]),
        };
    }

    private static string Camel(FieldType type) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(type.ToString());
}
