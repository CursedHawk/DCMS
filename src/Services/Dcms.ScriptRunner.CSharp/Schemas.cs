using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

/// <summary>
/// A C# function's <c>TIn</c> and <c>TOut</c> as JSON Schema: what the site's OpenAPI document
/// says it takes and returns, and what admin-api checks HTTP calls against. It follows how the
/// SDK reads and writes them (System.Text.Json's web defaults: camelCase, enums as numbers).
/// A type it cannot say (object, JsonNode, a respond(...) result) is <c>{}</c>: any JSON.
/// </summary>
internal static partial class Schemas
{
    private const int MaxDepth = 8;
    private const int MaxNodes = 1_000;
    private const int MaxChars = 32_000;

    public static JsonObject Of(ITypeSymbol? type)
    {
        if (type is null) return [];
        var budget = new int[1];
        var schema = Walk(type, 0, [], budget);
        return budget[0] > MaxNodes || schema.ToJsonString().Length > MaxChars ? [] : schema;
    }

    private static JsonObject Walk(ITypeSymbol type, int depth, HashSet<ITypeSymbol> seen, int[] budget)
    {
        if (depth > MaxDepth || ++budget[0] > MaxNodes || seen.Contains(type)) return [];

        // int?, and string? where the code says it may be null.
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            return Nullable(Walk(nullable.TypeArguments[0], depth, seen, budget));
        }
        if (type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated)
        {
            return Nullable(Walk(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated), depth, seen, budget));
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_String or SpecialType.System_Char: return new JsonObject { ["type"] = "string" };
            case SpecialType.System_Boolean: return new JsonObject { ["type"] = "boolean" };
            case SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64:
                return new JsonObject { ["type"] = "integer" };
            case SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal:
                return new JsonObject { ["type"] = "number" };
            case SpecialType.System_DateTime: return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
            case SpecialType.System_Object: return [];
        }

        switch (type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", ""))
        {
            case "System.Guid": return new JsonObject { ["type"] = "string", ["format"] = "uuid" };
            case "System.DateTimeOffset": return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
            case "System.DateOnly": return new JsonObject { ["type"] = "string", ["format"] = "date" };
            case "System.TimeOnly" or "System.TimeSpan": return new JsonObject { ["type"] = "string" };
            case "System.Uri": return new JsonObject { ["type"] = "string", ["format"] = "uri" };
            case "System.Text.Json.Nodes.JsonObject": return new JsonObject { ["type"] = "object" };
            case "System.Text.Json.Nodes.JsonArray": return new JsonObject { ["type"] = "array" };
        }
        if (type.ContainingNamespace?.ToDisplayString() is "System.Text.Json.Nodes" or "System.Text.Json" or "Dcms.Functions") return [];

        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol e)
        {
            var values = new JsonArray();
            foreach (var field in e.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
            {
                values.Add(JsonValue.Create(Convert.ToInt64(field.ConstantValue, System.Globalization.CultureInfo.InvariantCulture)));
            }
            return new JsonObject { ["type"] = "integer", ["enum"] = values };
        }

        var inner = new HashSet<ITypeSymbol>(seen, SymbolEqualityComparer.Default) { type };
        if (type is IArrayTypeSymbol array)
        {
            return new JsonObject { ["type"] = "array", ["items"] = Walk(array.ElementType, depth + 1, inner, budget) };
        }
        if (Dictionary(type) is { } entry)
        {
            return new JsonObject { ["type"] = "object", ["additionalProperties"] = Walk(entry, depth + 1, inner, budget) };
        }
        if (Sequence(type) is { } item)
        {
            return new JsonObject { ["type"] = "array", ["items"] = Walk(item, depth + 1, inner, budget) };
        }
        if (type is not INamedTypeSymbol named || type.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface)) return [];

        var properties = new JsonObject();
        var required = new JsonArray();
        var positional = named.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public)
            .MaxBy(c => c.Parameters.Length)?.Parameters
            .Where(p => !p.HasExplicitDefaultValue)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var t = named; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (var property in t.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public || property.GetMethod is null
                    || property.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonIgnoreAttribute")
                    || property.IsImplicitlyDeclared)
                {
                    continue;
                }
                var name = property.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "JsonPropertyNameAttribute")?
                    .ConstructorArguments.FirstOrDefault().Value as string ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                if (!names.Add(name)) continue;
                var schema = Walk(property.Type, depth + 1, inner, budget);
                if (Summary(property) is { } summary) schema["description"] = summary;
                properties[name] = schema;
                var mayBeNull = property.Type.NullableAnnotation == NullableAnnotation.Annotated
                               || property.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
                if (property.IsRequired || property.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonRequiredAttribute")
                    || (positional.Contains(property.Name) && !mayBeNull))
                {
                    required.Add(name);
                }
            }
        }
        var result = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) result["required"] = required;
        return result;
    }

    private static JsonObject Nullable(JsonObject schema)
    {
        if (schema.Count == 0) return schema;
        if (schema["type"] is JsonValue type && type.GetValue<string>() is var name)
        {
            schema["type"] = new JsonArray(name, "null");
            if (schema["enum"] is JsonArray values) values.Add(null);
            return schema;
        }
        return new JsonObject { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) };
    }

    /// <summary>The value type of a string-keyed dictionary.</summary>
    private static ITypeSymbol? Dictionary(ITypeSymbol type) =>
        Implementations(type).FirstOrDefault(i => i.OriginalDefinition.ToDisplayString() is
                "System.Collections.Generic.IDictionary<TKey, TValue>" or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
            && i.TypeArguments[0].SpecialType == SpecialType.System_String)?.TypeArguments[1];

    private static ITypeSymbol? Sequence(ITypeSymbol type) =>
        Implementations(type).FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)?.TypeArguments[0];

    private static IEnumerable<INamedTypeSymbol> Implementations(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Interface } self ? [self, .. type.AllInterfaces] : type.AllInterfaces;

    private static string? Summary(ISymbol symbol) =>
        symbol.GetDocumentationCommentXml() is { Length: > 0 } xml && SummaryText().Match(xml) is { Success: true } m
            ? Whitespace().Replace(Tags().Replace(m.Groups[1].Value, ""), " ").Trim() is { Length: > 0 } text ? text[..Math.Min(text.Length, 500)] : null
            : null;

    [GeneratedRegex("<summary>(.*?)</summary>", RegexOptions.Singleline)]
    private static partial Regex SummaryText();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
