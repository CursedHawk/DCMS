using System.Text.Json;
using System.Text.RegularExpressions;
using Dcms.Plugins.DynamicApps.Api.Model;
using NpgsqlTypes;

namespace Dcms.Plugins.DynamicApps.Data;

/// <summary>Writes a SQL predicate, adding each value it needs through <c>parameter</c>, which returns the placeholder.</summary>
public delegate string SqlWriter(Func<object, NpgsqlDbType, string> parameter);

/// <summary>
/// The signed-in site user as row rules see them: only what they cannot claim for themselves.
/// <paramref name="Email"/> and <paramref name="Groups"/> come from a User Authentication sign-in
/// alone (a VisitorAuth visitor registers with any address, unverified); <paramref name="Attributes"/>
/// holds only attributes the visitor may not edit.
/// </summary>
public sealed record RowSubject(Guid Id, string? Email, IReadOnlyList<Guid> Groups, IReadOnlyDictionary<string, JsonElement> Attributes)
{
    /// <summary>What <paramref name="matches"/> names of this user, lower-cased; empty when they have none.</summary>
    public string[] Values(string matches)
    {
        IEnumerable<string> values = matches switch
        {
            "user.id" => [Id.ToString()],
            "user.email" => Email is null ? [] : [Email],
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
    /// <remarks>
    /// Each hop is <c>(x IN (uncorrelated subquery)) IS TRUE</c>. The subquery does not refer to the
    /// outer row (tenant and instance are parameters), and the <c>IS TRUE</c> keeps the planner
    /// from turning it into a semi-join, so Postgres builds the set once as a hashed SubPlan and
    /// probes it per record. As a semi-join under a page's ORDER BY … LIMIT it nested-looped:
    /// 10 companies for a user with 2,500 grants took 2.3 million comparisons, 0.7 s on vps1
    /// (12 ms hashed); as `@>` containment before that, 15 s.
    /// ponytail: a hashed SubPlan needs the set to fit in hash memory (work_mem × hash_mem_multiplier);
    /// a user reaching hundreds of thousands of records falls back to a rescan per record, no worse
    /// than the nested loop it replaces. Raise work_mem for such tenants if that ever happens.
    /// </remarks>
    public static string Sql(RuntimeModel model, RuntimeTable table, RowRule rule, RowSubject subject, string alias,
        Guid tenantId, Guid instanceId, Func<object, NpgsqlDbType, string> parameter)
    {
        var values = subject.Values(rule.Matches);
        return values.Length == 0 ? "FALSE" : Step(model, table, rule, values, alias, 0, tenantId, instanceId, parameter);
    }

    private static string Step(RuntimeModel model, RuntimeTable table, RowRule rule, string[] values, string alias, int depth,
        Guid tenantId, Guid instanceId, Func<object, NpgsqlDbType, string> parameter)
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
        var tenant = parameter(tenantId, NpgsqlDbType.Uuid);
        var instance = parameter(instanceId, NpgsqlDbType.Uuid);
        var records = $"""
            apps.records {next} WHERE {next}."TenantId" = {tenant} AND {next}."InstanceId" = {instance}
            AND {next}."TableId" = {parameter(other.Id, NpgsqlDbType.Uuid)}
            """;
        var inner = Step(model, other, rule, values, next, depth + 1, tenantId, instanceId, parameter);
        var relationship = parameter(nav.Relationship.Id.ToString(), NpgsqlDbType.Text);
        return nav.Kind switch
        {
            // The record's lookup names the next record.
            NavigationKind.Lookup => $"""(({alias}."Data" ->> {relationship}) IN (SELECT {next}."Id"::text FROM {records} AND {inner})) IS TRUE""",
            // The next record's lookup names this one.
            NavigationKind.Inverse => $"""({alias}."Id"::text IN (SELECT {next}."Data" ->> {relationship} FROM {records} AND {inner})) IS TRUE""",
            _ => $"""
                ({alias}."Id" IN (SELECT rl{depth}."{(nav.FromSource ? "SourceId" : "TargetId")}" FROM apps.relation_links rl{depth}
                WHERE rl{depth}."TenantId" = {tenant} AND rl{depth}."InstanceId" = {instance}
                AND rl{depth}."RelationshipId" = {parameter(nav.Relationship.Id, NpgsqlDbType.Uuid)}
                AND (rl{depth}."{(nav.FromSource ? "TargetId" : "SourceId")}" IN (SELECT {next}."Id" FROM {records} AND {inner})) IS TRUE)) IS TRUE
                """,
        };
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex AttributeKey();
}
