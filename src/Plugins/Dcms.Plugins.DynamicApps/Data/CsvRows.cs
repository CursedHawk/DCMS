using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.DynamicApps.Data;

/// <summary>
/// CSV text as import rows (RFC 4180: comma or semicolon separated, double-quoted cells may hold
/// separators, quotes doubled, and line breaks). The first row names the columns: a field's api
/// name or display name, or whatever <c>columns</c> maps it to. Cells are turned into the field's
/// type here; whether the value is acceptable is still <see cref="RecordCodec"/>'s call.
/// </summary>
public static class CsvRows
{
    public const int MaxChars = 2_000_000;
    public const int MaxColumns = 200;

    /// <exception cref="ContractValidationException">The text is not usable CSV, or names a column the table does not have.</exception>
    public static IReadOnlyList<JsonObject> Parse(RuntimeTable table, string csv, IReadOnlyDictionary<string, string>? columns, int maxRows)
    {
        if (csv.Length > MaxChars)
        {
            throw new ContractValidationException($"The CSV is larger than {MaxChars:N0} characters; split it.");
        }
        var lines = Split(csv.TrimStart('﻿'), maxRows + 1);
        if (lines.Count < 2)
        {
            throw new ContractValidationException("The CSV needs a header row and at least one row of values.");
        }
        var header = lines[0];
        if (header.Count > MaxColumns)
        {
            throw new ContractValidationException($"The CSV has more than {MaxColumns} columns.");
        }
        var members = header.Select(h => Column(table, h.Trim(), columns)).ToList();
        var unknown = header.Where((_, i) => members[i] is null).ToList();
        if (unknown.Count > 0)
        {
            throw new ContractValidationException(
                $"{table.ApiName} has no field for column(s) {string.Join(", ", unknown.Select(u => $"'{u}'"))}. "
                + $"Its fields: {string.Join(", ", table.Members.Select(m => m.ApiName))}. Rename the columns or map them with columns.");
        }

        var rows = new List<JsonObject>();
        foreach (var (cells, line) in lines.Skip(1).Select((c, i) => (c, i + 1)))
        {
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }
            // More cells than columns is almost always an unquoted separator inside a value:
            // dropping the extra would quietly cut the value short.
            if (cells.Skip(header.Count).Any(c => !string.IsNullOrWhiteSpace(c)))
            {
                throw new ContractValidationException(
                    $"Line {line + 1} has {cells.Count} cells but the header names {header.Count} columns. Quote values that contain the separator.");
            }
            var row = new JsonObject();
            for (var i = 0; i < Math.Min(cells.Count, members.Count); i++)
            {
                if (members[i] is { } member && Value(member, cells[i]) is { } value)
                {
                    row[member.ApiName] = value;
                }
            }
            rows.Add(row);
        }
        return rows;
    }

    private static RuntimeMember? Column(RuntimeTable table, string header, IReadOnlyDictionary<string, string>? columns)
    {
        if (columns is not null && columns.TryGetValue(header, out var mapped))
        {
            header = mapped;
        }
        return table.ByName.GetValueOrDefault(header)
               ?? table.Members.FirstOrDefault(m => string.Equals(m.ApiName, header, StringComparison.OrdinalIgnoreCase)
                                                    || string.Equals(m.Field?.DisplayName ?? m.Lookup?.DisplayName, header, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A cell as the JSON the field takes; null for an empty cell. Unparseable text is kept as text for the codec to refuse.</summary>
    private static JsonNode? Value(RuntimeMember member, string cell)
    {
        var text = cell.Trim();
        if (text.Length == 0)
        {
            return null;
        }
        if (member.IsLookup)
        {
            return text; // an id, or the target's primary field value: resolved by the import
        }
        switch (member.Field!.Type)
        {
            case FieldType.Integer or FieldType.Decimal:
                return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? JsonValue.Create(n) : text;
            case FieldType.Boolean:
                return text.ToLowerInvariant() switch
                {
                    "true" or "yes" or "1" or "y" => true,
                    "false" or "no" or "0" or "n" => false,
                    _ => text,
                };
            case FieldType.Choice:
                return member.Options?.FirstOrDefault(o => string.Equals(o, text, StringComparison.OrdinalIgnoreCase)) ?? text;
            case FieldType.MultiChoice:
                return new JsonArray(text.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(c => (JsonNode)(member.Options?.FirstOrDefault(o => string.Equals(o, c, StringComparison.OrdinalIgnoreCase)) ?? c))
                    .ToArray());
            case FieldType.Json:
                try
                {
                    return JsonNode.Parse(text);
                }
                catch (System.Text.Json.JsonException)
                {
                    return text;
                }
            default:
                return text;
        }
    }

    /// <summary>The records of the text, at most <paramref name="maxRecords"/>; the separator is whichever of , and ; the header uses more.</summary>
    private static List<List<string>> Split(string text, int maxRecords)
    {
        var firstLine = text.Split('\n', 2)[0];
        var separator = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';
        var records = new List<List<string>>();
        var record = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
                continue;
            }
            if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == separator)
            {
                record.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                record.Add(cell.ToString());
                cell.Clear();
                records.Add(record);
                record = [];
                if (records.Count > maxRecords)
                {
                    throw new ContractValidationException($"An import takes at most {maxRecords - 1} rows; split the file.");
                }
            }
            else
            {
                cell.Append(c);
            }
        }
        if (quoted)
        {
            throw new ContractValidationException("The CSV ends inside a quoted cell.");
        }
        if (cell.Length > 0 || record.Count > 0)
        {
            record.Add(cell.ToString());
            records.Add(record);
        }
        if (records.Count > maxRecords)
        {
            throw new ContractValidationException($"An import takes at most {maxRecords - 1} rows; split the file.");
        }
        return records;
    }
}
