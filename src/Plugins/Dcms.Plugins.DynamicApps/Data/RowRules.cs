using System.Text.Json;
using System.Text.RegularExpressions;
using Dcms.Plugins.DynamicApps.Api.Model;
using NpgsqlTypes;

namespace Dcms.Plugins.DynamicApps.Data;

/// <summary>Writes a SQL predicate, adding each value it needs through <c>parameter</c>, which returns the placeholder.</summary>
public delegate string SqlWriter(Func<object, NpgsqlDbType, string> parameter);

/// <summary>The signed-in site user as row rules see them (a VisitorAuth visitor or a User Authentication user).</summary>
public sealed record RowSubject(Guid Id, string Email, IReadOnlyList<Guid> Groups, IReadOnlyDictionary<string, JsonElement> Attributes)
{
    /// <summary>What <paramref name="matches"/> names of this user, lower-cased; empty when they have none.</summary>
    public string[] Values(string matches)
    {
        IEnumerable<string> values = matches switch
        {
            "user.id" => [Id.ToString()],
            "user.email" => [Email],
            "user.groups" => Groups.Select(g => g.ToString()),
            _ when matches.StartsWith(RowRules.AttributePrefix, StringComparison.Ordinal)
                   && Attributes.TryGetValue(matches[RowRules.AttributePrefix.Length..], out var value) => Scalars(value),
            _ => [],
        };
        return values.Where(v => v.Length is > 0 and <= 1000).Select(v => v.ToLowerInvariant()).Distinct().Take(RowRules.MaxValues).ToArray();
    }

    private static IEnumerable<string> Scalars(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => [value.GetString()!],
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => [value.GetRawText()],
        JsonValueKind.Array => value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!),
        _ => [],
    };
}

/// <summary>
/// Row access rules compiled to SQL (ADR 0021): from a record, follow the rule's navigations and
/// test one field against the signed-in user. The SQL text holds only this class's identifiers;
/// table ids, member keys and the user's values are all parameters.
/// </summary>
public static partial class RowRules
{
    public const int MaxRules = 10;
    public const int MaxPath = 3;

    /// <summary>The most values of one user a rule compares against (their groups, say).</summary>
    public const int MaxValues = 200;

    public const string AttributePrefix = "user.attribute.";

    public static readonly IReadOnlyList<string> Subjects = ["user.id", "user.email", "user.groups"];

    /// <summary>The field types a rule can test: text-like values, compared without regard to case.</summary>
    public static readonly IReadOnlySet<FieldType> FieldTypes = new HashSet<FieldType>
    {
        FieldType.Text, FieldType.Email, FieldType.Choice, FieldType.MultiChoice,
    };

    public static bool ValidSubject(string matches) =>
        Subjects.Contains(matches)
        || (matches.StartsWith(AttributePrefix, StringComparison.Ordinal) && AttributeKey().IsMatch(matches[AttributePrefix.Length..]));

    /// <summary>
    /// The rule over the record at <paramref name="alias"/>; <c>FALSE</c> when it cannot match —
    /// the user has none of the values, or the path leads through a table that is not enabled.
    /// </summary>
    public static string Sql(RuntimeModel model, RuntimeTable table, RowRule rule, RowSubject subject, string alias,
        Func<object, NpgsqlDbType, string> parameter)
    {
        var values = subject.Values(rule.Matches);
        return values.Length == 0 ? "FALSE" : Step(model, table, rule, values, alias, 0, parameter);
    }

    private static string Step(RuntimeModel model, RuntimeTable table, RowRule rule, string[] values, string alias, int depth,
        Func<object, NpgsqlDbType, string> parameter)
    {
        if (depth == rule.Path.Count)
        {
            if (!table.ByName.TryGetValue(rule.Field, out var member) || member.Field is not { } field || !FieldTypes.Contains(field.Type))
            {
                return "FALSE";
            }
            var key = parameter(member.Key, NpgsqlDbType.Text);
            var list = parameter(values, NpgsqlDbType.Array | NpgsqlDbType.Text);
            return field.Type == FieldType.MultiChoice
                ? $"""EXISTS (SELECT 1 FROM jsonb_array_elements_text(CASE WHEN jsonb_typeof({alias}."Data" -> {key}) = 'array' THEN {alias}."Data" -> {key} ELSE '[]'::jsonb END) v WHERE lower(v) = ANY({list}))"""
                : $"""lower({alias}."Data" ->> {key}) = ANY({list})""";
        }

        if (!table.Navigations.TryGetValue(rule.Path[depth], out var nav) || !model.ById.TryGetValue(nav.OtherTableId, out var other))
        {
            return "FALSE";
        }
        var next = $"ra{depth}";
        var relationship = parameter(nav.Relationship.Id.ToString(), NpgsqlDbType.Text);
        var join = nav.Kind switch
        {
            NavigationKind.Lookup => $"""{next}."Id"::text = ({alias}."Data" ->> {relationship})""",
            NavigationKind.Inverse => $"""{next}."Data" @> jsonb_build_object({relationship}::text, {alias}."Id"::text)""",
            _ => $"""
                EXISTS (SELECT 1 FROM apps.relation_links rl{depth} WHERE rl{depth}."TenantId" = {alias}."TenantId"
                AND rl{depth}."InstanceId" = {alias}."InstanceId" AND rl{depth}."RelationshipId" = {parameter(nav.Relationship.Id, NpgsqlDbType.Uuid)}
                AND rl{depth}."{(nav.FromSource ? "SourceId" : "TargetId")}" = {alias}."Id" AND rl{depth}."{(nav.FromSource ? "TargetId" : "SourceId")}" = {next}."Id")
                """,
        };
        var inner = Step(model, other, rule, values, next, depth + 1, parameter);
        return $"""
            EXISTS (SELECT 1 FROM apps.records {next} WHERE {next}."TenantId" = {alias}."TenantId" AND {next}."InstanceId" = {alias}."InstanceId"
            AND {next}."TableId" = {parameter(other.Id, NpgsqlDbType.Uuid)} AND {join} AND {inner})
            """;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex AttributeKey();
}
