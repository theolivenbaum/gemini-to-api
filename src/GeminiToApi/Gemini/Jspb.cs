using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GeminiToApi.Gemini;

/// <summary>
/// Helpers for Google's JSPB wire format: positional JSON arrays where a field is an index, and
/// trailing objects hold sparse fields keyed by their one-based field number.
/// </summary>
internal static class Jspb
{
    /// <summary>Serializer options matching Go's json.Marshal closely enough for Google (raw UTF-8).</summary>
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(JsonNode? node) => node is null ? "null" : node.ToJsonString(WriteOptions);

    public static JsonArray NewArray(int length) => new(new JsonNode?[length]);

    public static JsonArray Ints(params int[] values) => new(values.Select(v => (JsonNode?)v).ToArray());

    public static JsonArray Of(params JsonNode?[] values) => new(values);

    public static void EnsureLength(JsonArray array, int length)
    {
        while (array.Count < length) array.Add(null);
    }

    public static JsonNode? At(this JsonNode? node, int index)
        => node is JsonArray a && index >= 0 && index < a.Count ? a[index] : null;

    public static JsonArray? AsArr(this JsonNode? node) => node as JsonArray;

    public static string? Str(this JsonNode? node)
        => node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public static bool TryDouble(this JsonNode? node, out double value)
    {
        value = 0;

        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return false;

        if (v.TryGetValue(out double d)) { value = d; return true; }
        if (v.TryGetValue(out long l)) { value = l; return true; }
        if (v.TryGetValue(out int i)) { value = i; return true; }
        if (v.TryGetValue(out decimal m)) { value = (double)m; return true; }

        return double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Integer value of a JSON number (fractional numbers are truncated, like Go's int(float64)).</summary>
    public static bool TryInt(this JsonNode? node, out int value)
    {
        value = 0;

        if (!node.TryDouble(out var d)) return false;

        value = (int)d;
        return true;
    }

    /// <summary>Reads a zero-based field, including fields stored in a trailing sparse object.</summary>
    public static JsonNode? Field(this JsonNode? node, int index)
    {
        if (node is not JsonArray a || index < 0) return null;

        if (index < a.Count && a[index] is not null and not JsonObject) return a[index];

        if (a.Count > 0 && a[^1] is JsonObject sparse) return sparse[(index + 1).ToString()];

        return null;
    }

    public static JsonNode? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes a sequence of concatenated JSON values (batchexecute's length-prefixed frames after the
    /// XSSI guard). Throws <see cref="JsonException"/> on malformed input.
    /// </summary>
    public static List<JsonNode?> ParseMany(ReadOnlySpan<byte> utf8)
    {
        var values = new List<JsonNode?>();
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { AllowMultipleValues = true });

        while (reader.Read())
        {
            values.Add(JsonNode.Parse(ref reader));
        }

        return values;
    }

    /// <summary>Removes the <c>)]}'</c> XSSI guard and surrounding whitespace.</summary>
    public static string StripXssi(string text)
    {
        text = text.Trim();
        return text.StartsWith(")]}'", StringComparison.Ordinal) ? text[4..] : text;
    }
}
