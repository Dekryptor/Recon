using System.Text.Json;
using System.Text.Json.Nodes;

namespace Recon.Schema;

public sealed record SchemaViolation(string Path, string Message)
{
    public override string ToString() => string.IsNullOrEmpty(Path) ? Message : $"{Path}: {Message}";
}

/// <summary>
/// A small JSON Schema validator covering the keywords this project's schemas use:
/// type, required, properties, additionalProperties, items, enum, const, pattern, minimum/maximum,
/// anyOf/oneOf/allOf, and local <c>$ref</c>. It exists so that <c>recon validate</c> and the golden
/// tests can check the inventory against its published contract without pulling in a dependency.
/// </summary>
/// <remarks>
/// The schema is held as a <see cref="JsonNode"/> graph — schemas are small and resolving a
/// <c>$ref</c> is a walk over one. The document is not: it is validated as a
/// <see cref="JsonElement"/> over the parsed bytes, which is read-only and costs a fraction of the
/// document's size. The earlier version parsed the document into a node graph as well, which is a
/// mutable object per value: on a 51 MB inventory that alone was enough to have the process killed,
/// so the check that is supposed to prove a document is well-formed could not be run on the
/// documents most worth checking.
/// </remarks>
public sealed class JsonSchemaValidator
{
    private readonly JsonObject _root;

    private JsonSchemaValidator(JsonObject root) => _root = root;

    public static JsonSchemaValidator Parse(string schemaJson)
    {
        var node = JsonNode.Parse(schemaJson) as JsonObject
                   ?? throw new InvalidOperationException("schema root must be an object");
        return new JsonSchemaValidator(node);
    }

    public static JsonSchemaValidator FromFile(string path) => Parse(File.ReadAllText(path));

    /// <summary>Validates a document, which the caller has already parsed.</summary>
    public List<SchemaViolation> Validate(JsonElement instance)
    {
        var violations = new List<SchemaViolation>();
        ValidateNode(_root, instance, string.Empty, violations, depth: 0);
        return violations;
    }

    /// <summary>Parses a document and validates it, disposing the parse when it is done.</summary>
    public List<SchemaViolation> Validate(string instanceJson)
    {
        using var document = JsonDocument.Parse(instanceJson);
        return Validate(document.RootElement);
    }

    private void ValidateNode(JsonObject schema, JsonElement instance, string path, List<SchemaViolation> violations, int depth)
    {
        if (depth > 64)
        {
            violations.Add(new SchemaViolation(path, "schema nesting is too deep"));
            return;
        }

        if (schema.TryGetPropertyValue("$ref", out var reference) && reference is JsonValue referenceValue)
        {
            var target = ResolveRef(referenceValue.GetValue<string>());
            if (target is null)
            {
                violations.Add(new SchemaViolation(path, $"unresolvable $ref {referenceValue}"));
                return;
            }

            ValidateNode(target, instance, path, violations, depth + 1);
            return;
        }

        if (schema.TryGetPropertyValue("type", out var typeNode) && typeNode is JsonValue typeValue)
        {
            string expected = typeValue.GetValue<string>();
            if (!MatchesType(instance, expected))
            {
                violations.Add(new SchemaViolation(path, $"expected {expected}, found {Describe(instance)}"));
                return;
            }
        }

        if (schema.TryGetPropertyValue("const", out var constNode) && constNode is not null)
        {
            if (!DeepEquals(constNode, instance))
            {
                violations.Add(new SchemaViolation(path, $"must equal {constNode.ToJsonString()}"));
            }
        }

        if (schema.TryGetPropertyValue("enum", out var enumNode) && enumNode is JsonArray enumArray)
        {
            if (!enumArray.Any(candidate => DeepEquals(candidate, instance)))
            {
                violations.Add(new SchemaViolation(path, $"must be one of {enumNode.ToJsonString()}"));
            }
        }

        if (schema.TryGetPropertyValue("pattern", out var patternNode) && patternNode is JsonValue patternValue
            && instance.ValueKind == JsonValueKind.String)
        {
            string? text = instance.GetString();
            if (text is not null && !System.Text.RegularExpressions.Regex.IsMatch(text, patternValue.GetValue<string>()))
            {
                violations.Add(new SchemaViolation(path, $"must match {patternValue.GetValue<string>()}"));
            }
        }

        if (instance.ValueKind == JsonValueKind.Number)
        {
            double number = instance.GetDouble();
            if (schema.TryGetPropertyValue("minimum", out var minNode) && minNode is JsonValue minValue && number < minValue.GetValue<double>())
            {
                violations.Add(new SchemaViolation(path, $"must be >= {minValue.GetValue<double>()}"));
            }

            if (schema.TryGetPropertyValue("maximum", out var maxNode) && maxNode is JsonValue maxValue && number > maxValue.GetValue<double>())
            {
                violations.Add(new SchemaViolation(path, $"must be <= {maxValue.GetValue<double>()}"));
            }
        }

        if (instance.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetPropertyValue("required", out var requiredNode) && requiredNode is JsonArray required)
            {
                foreach (var name in required)
                {
                    string? key = name?.GetValue<string>();
                    if (key is not null && !instance.TryGetProperty(key, out _))
                    {
                        violations.Add(new SchemaViolation(path, $"missing required property \"{key}\""));
                    }
                }
            }

            if (schema.TryGetPropertyValue("properties", out var propertiesNode) && propertiesNode is JsonObject properties)
            {
                foreach (var property in instance.EnumerateObject())
                {
                    if (properties.TryGetPropertyValue(property.Name, out var propertySchema) && propertySchema is JsonObject propertySchemaObject)
                    {
                        ValidateNode(propertySchemaObject, property.Value, Join(path, property.Name), violations, depth + 1);
                    }
                }
            }

            if (schema.TryGetPropertyValue("additionalProperties", out var additionalNode))
            {
                bool allowsAdditional = additionalNode is not JsonValue allowsValue || allowsValue.GetValue<bool>();
                if (!allowsAdditional && schema.TryGetPropertyValue("properties", out var knownNode) && knownNode is JsonObject known)
                {
                    foreach (var property in instance.EnumerateObject())
                    {
                        if (!known.ContainsKey(property.Name))
                        {
                            violations.Add(new SchemaViolation(path, $"unexpected property \"{property.Name}\""));
                        }
                    }
                }
            }
        }

        if (instance.ValueKind == JsonValueKind.Array && schema.TryGetPropertyValue("items", out var itemsNode) && itemsNode is JsonObject items)
        {
            int index = 0;
            foreach (var element in instance.EnumerateArray())
            {
                ValidateNode(items, element, $"{path}[{index}]", violations, depth + 1);
                index++;
            }
        }

        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (!schema.TryGetPropertyValue(keyword, out var alternativesNode) || alternativesNode is not JsonArray alternatives)
            {
                continue;
            }

            int matches = 0;
            foreach (var alternative in alternatives.OfType<JsonObject>())
            {
                var local = new List<SchemaViolation>();
                ValidateNode(alternative, instance, path, local, depth + 1);
                if (local.Count == 0)
                {
                    matches++;
                }
            }

            if ((keyword == "oneOf" && matches != 1) || (keyword == "anyOf" && matches == 0) || (keyword == "allOf" && matches != alternatives.Count))
            {
                violations.Add(new SchemaViolation(path, $"{keyword} constraint not satisfied"));
            }
        }
    }

    /// <summary>
    /// Whether a schema's constant or enum member equals what the document holds. Compared through
    /// the parsed forms rather than through text, so <c>1.0</c> and <c>1</c> are the same number and
    /// key order in an object does not matter.
    /// </summary>
    private static bool DeepEquals(JsonNode? expected, JsonElement actual)
    {
        switch (expected)
        {
            case null:
                return actual.ValueKind == JsonValueKind.Null;

            case JsonValue value when value.TryGetValue(out bool flag):
                return actual.ValueKind is JsonValueKind.True or JsonValueKind.False && actual.GetBoolean() == flag;

            case JsonValue value when value.TryGetValue(out long whole):
                return actual.ValueKind == JsonValueKind.Number && actual.TryGetInt64(out long other) && other == whole;

            case JsonValue value when value.TryGetValue(out double real):
                return actual.ValueKind == JsonValueKind.Number && actual.GetDouble().Equals(real);

            case JsonValue value when value.TryGetValue(out string? text):
                return actual.ValueKind == JsonValueKind.String && string.Equals(actual.GetString(), text, StringComparison.Ordinal);

            case JsonArray array:
            {
                if (actual.ValueKind != JsonValueKind.Array || actual.GetArrayLength() != array.Count)
                {
                    return false;
                }

                int index = 0;
                foreach (var element in actual.EnumerateArray())
                {
                    if (!DeepEquals(array[index], element))
                    {
                        return false;
                    }

                    index++;
                }

                return true;
            }

            case JsonObject expectedObject:
            {
                if (actual.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                int count = 0;
                foreach (var property in actual.EnumerateObject())
                {
                    count++;
                    if (!expectedObject.TryGetPropertyValue(property.Name, out var member) || !DeepEquals(member, property.Value))
                    {
                        return false;
                    }
                }

                return count == expectedObject.Count;
            }

            default:
                return false;
        }
    }

    private static string Describe(JsonElement instance) => instance.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "unknown",
    };

    private JsonObject? ResolveRef(string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }

        JsonNode? current = _root;
        foreach (var segment in reference[2..].Split('/'))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current as JsonObject;
    }

    private static bool MatchesType(JsonElement instance, string expected) => expected switch
    {
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        // An integer is a number with no fractional part and no exponent: 1.0 is an integer to JSON
        // Schema, and the inventory writes counts as integers.
        "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out _),
        "number" => instance.ValueKind == JsonValueKind.Number,
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => instance.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static string Join(string path, string key) => string.IsNullOrEmpty(path) ? key : $"{path}.{key}";
}
