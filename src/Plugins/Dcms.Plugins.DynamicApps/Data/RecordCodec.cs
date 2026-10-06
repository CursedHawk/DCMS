using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Shared.Data.DynamicApps;

namespace Dcms.Plugins.DynamicApps.Data;

/// <summary>Who is reading or writing a record, which decides what they see and may set.</summary>
public enum RecordPlane
{
    /// <summary>A member in the admin, or the assistant acting for one.</summary>
    Admin,

    /// <summary>The public site: hidden fields stay hidden, deprecated ones are not writable.</summary>
    Public,

    /// <summary>An automation: may also set read-only fields.</summary>
    System,
}

/// <summary>The public site may not do this: <see cref="Status"/> is the HTTP answer (401, 403 or 404).</summary>
public sealed class PublicAccessException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>A record failed validation; <see cref="Errors"/> says why, per field.</summary>
public sealed class RecordValidationException(IReadOnlyDictionary<string, string> errors)
    : Exception("The record is not valid: " + string.Join("; ", errors.Select(e => $"{e.Key}: {e.Value}")))
{
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}

/// <summary>
/// Turns API values (api names, loose types) into stored data (field ids, normalized values)
/// and back. Every write goes through <see cref="Write"/>, whichever plane it comes from, so a
/// record in the database is always one the published model accepts.
/// </summary>
public static partial class RecordCodec
{
    /// <summary>The largest record, in serialized characters.</summary>
    public const int MaxDataChars = 256_000;

    public const int MaxMultiChoice = 100;

    /// <summary>Names every record has; never fields (the validator reserves them).</summary>
    public static readonly IReadOnlySet<string> SystemNames =
        new HashSet<string>(StringComparer.Ordinal) { "id", "version", "created_at", "updated_at", "created_by", "updated_by" };

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    // ------------------------------------------------------------------ read

    public static JsonObject Read(RuntimeTable table, AppRecord record, RecordPlane plane, IReadOnlyCollection<string>? select = null)
    {
        var data = JsonNode.Parse(record.Data) as JsonObject ?? [];
        var result = new JsonObject
        {
            ["id"] = record.Id.ToString(),
            ["version"] = record.Version,
            ["created_at"] = record.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["updated_at"] = record.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
        };
        if (plane != RecordPlane.Public)
        {
            result["created_by"] = record.CreatedBy;
            result["updated_by"] = record.UpdatedBy;
        }
        foreach (var member in table.Members)
        {
            if (!Visible(member, plane) || (select is { Count: > 0 } && !select.Contains(member.ApiName)))
            {
                continue;
            }
            result[member.ApiName] = data[member.Key]?.DeepClone();
        }
        return result;
    }

    public static bool Visible(RuntimeMember member, RecordPlane plane) =>
        plane != RecordPlane.Public || member.Field?.HiddenFromPublic != true;

    // ------------------------------------------------------------------ write

    /// <param name="Data">The record's stored data after the write.</param>
    /// <param name="Changed">Api names whose value the write changed.</param>
    /// <param name="ExpectedVersion">The <c>version</c> the caller sent, if any.</param>
    public sealed record WriteResult(JsonObject Data, IReadOnlyList<string> Changed, int? ExpectedVersion);

    /// <summary>Validates and merges <paramref name="input"/> into <paramref name="existing"/> (null for a new record).</summary>
    /// <exception cref="RecordValidationException">Any value is refused; every problem is listed.</exception>
    public static WriteResult Write(RuntimeTable table, JsonObject input, JsonObject? existing, RecordPlane plane)
    {
        // Bounds the work and the error report: a body cannot name more than the record can hold.
        if (input.Count > table.Members.Count + SystemNames.Count + 1)
        {
            throw new RecordValidationException(new Dictionary<string, string>
            {
                ["_record"] = $"names {input.Count} properties; {table.ApiName} has {table.Members.Count} fields.",
            });
        }
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var data = existing?.DeepClone().AsObject() ?? [];
        int? expectedVersion = null;

        foreach (var (key, value) in input)
        {
            if (key == "version")
            {
                if (value is JsonValue v && v.TryGetValue<int>(out var version))
                {
                    expectedVersion = version;
                }
                else if (value is not null)
                {
                    errors[key] = "must be the record's version number.";
                }
                continue;
            }
            if (SystemNames.Contains(key))
            {
                continue; // echoed back from a read; the platform sets these
            }
            if (!table.ByName.TryGetValue(key, out var member) || !Visible(member, plane))
            {
                errors[key] = $"{table.ApiName} has no field '{key}'.";
                continue;
            }
            if (member.Field?.ReadOnly == true && plane != RecordPlane.System)
            {
                errors[key] = "is read-only; only automations set it.";
                continue;
            }
            if (member.Field?.Deprecated == true && plane == RecordPlane.Public)
            {
                errors[key] = "is no longer accepted.";
                continue;
            }
            if (value is null)
            {
                data.Remove(member.Key);
                continue;
            }
            if (Normalize(member, value, out var normalized) is { } problem)
            {
                errors[key] = problem;
            }
            else
            {
                data[member.Key] = normalized;
            }
        }

        if (existing is null)
        {
            foreach (var field in table.Members.Where(m => m.Field?.Default is not null && !data.ContainsKey(m.Key)))
            {
                data[field.Key] = field.Field!.Default!.DeepClone();
            }
        }
        foreach (var member in table.Members.Where(m => m.Required && !data.ContainsKey(m.Key) && !errors.ContainsKey(m.ApiName)))
        {
            errors[member.ApiName] = "is required.";
        }
        if (errors.Count == 0 && data.ToJsonString().Length > MaxDataChars)
        {
            errors["_record"] = $"is larger than {MaxDataChars:N0} characters.";
        }
        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        var changed = table.Members
            .Where(m => !JsonNode.DeepEquals(existing?[m.Key], data[m.Key]))
            .Select(m => m.ApiName)
            .ToList();
        return new WriteResult(data, changed, expectedVersion);
    }

    /// <summary>The stored form of one value, or why it is refused.</summary>
    private static string? Normalize(RuntimeMember member, JsonNode value, out JsonNode? normalized)
    {
        normalized = null;
        string? text = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        if (member.IsLookup)
        {
            if (!Guid.TryParse(text, out var target))
            {
                return "must be the id of a record.";
            }
            normalized = target.ToString();
            return null;
        }

        var field = member.Field!;
        switch (field.Type)
        {
            case FieldType.Text:
            case FieldType.LongText:
            {
                var max = field.MaxLength ?? (field.Type == FieldType.Text ? 1_000 : 100_000);
                if (text is null)
                {
                    return "must be text.";
                }
                if (text.Length > max)
                {
                    return $"is longer than {max} characters.";
                }
                normalized = text;
                return null;
            }
            case FieldType.Email:
            {
                var email = text?.Trim();
                if (email is null || email.Length > (field.MaxLength ?? 320) || !EmailPattern().IsMatch(email))
                {
                    return "must be an email address.";
                }
                normalized = email;
                return null;
            }
            case FieldType.Url:
            {
                if (text is null || text.Length > (field.MaxLength ?? 2_048)
                    || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                {
                    return "must be an http or https address.";
                }
                normalized = text;
                return null;
            }
            case FieldType.Integer:
            case FieldType.Decimal:
            {
                if (value is not JsonValue n || !n.TryGetValue<decimal>(out var number))
                {
                    return "must be a number.";
                }
                if (field.Type == FieldType.Integer && (number != decimal.Truncate(number) || number is > long.MaxValue or < long.MinValue))
                {
                    return "must be a whole number.";
                }
                if (number < field.Minimum || number > field.Maximum)
                {
                    return $"must lie between {field.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "-∞"} and {field.Maximum?.ToString(CultureInfo.InvariantCulture) ?? "∞"}.";
                }
                normalized = field.Type == FieldType.Integer ? JsonValue.Create((long)number) : JsonValue.Create(number);
                return null;
            }
            case FieldType.Boolean:
                if (value is not JsonValue b || !b.TryGetValue<bool>(out var flag))
                {
                    return "must be true or false.";
                }
                normalized = flag;
                return null;
            case FieldType.Date:
                if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    return "must be a date, yyyy-MM-dd.";
                }
                normalized = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return null;
            case FieldType.DateTime:
                if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
                {
                    return "must be a date and time, ISO 8601.";
                }
                // One fixed-width UTC form, so stored values compare and sort as written.
                normalized = instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
                return null;
            case FieldType.Choice:
                if (text is null || !Options(member).Contains(text))
                {
                    return $"must be one of {string.Join(", ", Options(member))}.";
                }
                normalized = text;
                return null;
            case FieldType.MultiChoice:
            {
                if (value is not JsonArray items || items.Count > MaxMultiChoice)
                {
                    return $"must be a list of at most {MaxMultiChoice} choices.";
                }
                var options = Options(member);
                var chosen = new List<string>();
                foreach (var item in items)
                {
                    if (item is not JsonValue iv || !iv.TryGetValue<string>(out var choice) || !options.Contains(choice))
                    {
                        return $"may only hold {string.Join(", ", options)}.";
                    }
                    if (!chosen.Contains(choice))
                    {
                        chosen.Add(choice);
                    }
                }
                normalized = new JsonArray(chosen.Select(c => (JsonNode)c).ToArray());
                return null;
            }
            case FieldType.Media:
                if (!Guid.TryParse(text, out var asset))
                {
                    return "must be a media asset id.";
                }
                normalized = asset.ToString();
                return null;
            default:
                normalized = value.DeepClone();
                return null;
        }
    }

    private static IReadOnlyList<string> Options(RuntimeMember member) => member.Options ?? [];

    // ------------------------------------------------------------------ uniqueness

    /// <summary>
    /// The keys a record claims, one per unique constraint whose values are all present (an
    /// empty value claims nothing, so any number of records may leave a unique field blank).
    /// </summary>
    public static IEnumerable<(Guid ConstraintId, string Key)> UniqueKeys(RuntimeTable table, JsonObject data)
    {
        foreach (var constraint in table.Uniques)
        {
            var parts = constraint.Members.Select(m => data[m.Key] is { } v ? KeyPart(m, v) : null).ToList();
            if (parts.Any(p => p is null))
            {
                continue;
            }
            var key = parts.Count == 1 ? parts[0]! : new JsonArray(parts.Select(p => (JsonNode)p!).ToArray()).ToJsonString();
            yield return (constraint.Id, key.Length <= 200 ? key : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
        }
    }

    private static string KeyPart(RuntimeMember member, JsonNode value) => member.Field?.Type switch
    {
        // Addresses differ only in case for nobody.
        FieldType.Email => value.GetValue<string>().ToLowerInvariant(),
        FieldType.Integer or FieldType.Decimal => value.GetValue<decimal>().ToString("0.############################", CultureInfo.InvariantCulture),
        _ when value is JsonValue v && v.TryGetValue<string>(out var s) => s,
        _ => value.ToJsonString(),
    };
}
