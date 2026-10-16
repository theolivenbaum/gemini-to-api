using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GeminiToApi.Gemini;

/// <summary>
/// Model discovery from the account's GetUserStatus RPC (<c>otAQ7b</c>) and resolution of public model
/// names to the routing identity Gemini Web needs. Public names alone cannot select a model.
/// </summary>
internal static partial class GeminiModels
{
    public const string USER_STATUS_RPC = "otAQ7b";

    [GeneratedRegex(@"\b(\d+)(?:\.\d+)?\b")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"[^a-z0-9.]+")]
    private static partial Regex SlugSeparator();

    /// <summary>
    /// Accepts ordinary batchexecute JSON and the length-prefixed frames that follow Google's XSSI guard.
    /// Frames are decoded as a sequence of JSON values, so multi-line frames work too.
    /// </summary>
    public static List<GeminiModel> Parse(string body)
    {
        var text = Jspb.StripXssi(body);

        List<JsonNode?> values;

        try
        {
            values = Jspb.ParseMany(Encoding.UTF8.GetBytes(text));
        }
        catch (JsonException)
        {
            throw new GeminiException("invalid Gemini model discovery response");
        }

        var records = new List<JsonArray>();

        foreach (var value in values) CollectRecords(value, records);

        var models      = new List<GeminiModel>();
        var seen        = new HashSet<string>(StringComparer.Ordinal);
        var publicNames = new HashSet<string>(StringComparer.Ordinal);
        var created     = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var record in records)
        {
            if (record.At(5) is JsonArray reject && TryExactInt(reject.At(0), out var rejectCode) && rejectCode != 0)
                throw new GeminiException($"Gemini model discovery RPC rejected the session (code {rejectCode})");

            JsonArray status;

            switch (record.At(2))
            {
                case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                    status = Jspb.TryParse(v.GetValue<string>()) as JsonArray ?? throw new GeminiException("invalid Gemini user status response");
                    break;
                case JsonArray a:
                    status = a;
                    break;
                default:
                    throw new GeminiException("Gemini model discovery returned no user status");
            }

            var rawStatus = status.At(14);

            if (rawStatus is not null)
            {
                if (!TryExactInt(rawStatus, out var code)) throw new GeminiException("invalid Gemini account status");

                if (code != 1000)
                    throw new GeminiException($"Gemini account cannot select models (status {code}); verify access and cookies on Gemini Web");
            }

            var (capacity, capacityField) = AccountCapacity(status.At(16), status.At(17));

            if (status.At(15) is not JsonArray entries) continue;

            foreach (var entry in entries)
            {
                if (entry is not JsonArray fields) continue;

                var modelId = FirstString(fields, 0);

                if (modelId.Length == 0 || seen.Contains(modelId)) continue;

                var category = FirstString(fields, 1, 10);
                var display  = FirstString(fields, 11, 19, 1);

                var (name, aliases) = ModelNames(modelId, category, display);

                if (publicNames.Contains(name))
                {
                    name += "-" + (modelId.Length > 8 ? modelId[..8] : modelId).ToLowerInvariant();
                }

                var rawNumber   = fields.At(17) ?? fields.At(9);
                var modelNumber = 1; // protocol default when the RPC omits the field

                if (rawNumber is not null && (!TryExactInt(rawNumber, out modelNumber) || modelNumber <= 0)) continue;

                models.Add(new GeminiModel(new ModelInfo(name, display, created), modelId, capacity, capacityField, modelNumber, aliases));
                seen.Add(modelId);
                publicNames.Add(name);
            }
        }

        if (models.Count == 0) throw new GeminiException("Gemini model discovery returned no available models");

        return models;
    }

    private static void CollectRecords(JsonNode? value, List<JsonArray> records)
    {
        if (value is not JsonArray items) return;

        if (items.Count >= 2)
        {
            var marker = items.At(0).Str();

            if (marker is "wrb.fr" or "er")
            {
                if (items.At(1).Str() == USER_STATUS_RPC) records.Add(items);
                return;
            }
        }

        foreach (var item in items) CollectRecords(item, records);
    }

    /// <summary>
    /// Flag priorities match Gemini-API's AvailableModel.compute_capacity: the account's tier and
    /// capability flags, not the caller's model name, decide the capacity value and its field.
    /// </summary>
    public static (int Capacity, int Field) AccountCapacity(JsonNode? tierValue, JsonNode? capabilityValue)
    {
        var tiers        = tierValue as JsonArray;
        var capabilities = capabilityValue as JsonArray;

        static bool Has(JsonArray? flags, int want) => flags is not null && flags.Any(f => TryExactInt(f, out var n) && n == want);

        if (Has(tiers, 21)) return (1, 13);
        if (Has(tiers, 22)) return (2, 13);
        if (Has(capabilities, 115)) return (4, 12);
        if (Has(tiers, 16) || Has(capabilities, 106)) return (3, 12);
        if (Has(tiers, 8) || Has(capabilities, 19)) return (2, 12);

        return (1, 12);
    }

    public static (string Name, List<string> Aliases) ModelNames(string modelId, string category, string display)
    {
        var aliases = new SortedSet<string>(StringComparer.Ordinal);

        void Add(string value)
        {
            value = value.Trim().ToLowerInvariant();
            if (value.Length > 0) aliases.Add(value);
        }

        string Slug(string value) => SlugSeparator().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');

        static string Prefix(string value) => value.StartsWith("gemini-", StringComparison.Ordinal) ? value : "gemini-" + value;

        static string TrimGemini(string value) => value.StartsWith("gemini-", StringComparison.Ordinal) ? value[7..] : value;

        Add(modelId);

        foreach (var value in new[] { category, display })
        {
            if (value.Length == 0) continue;

            Add(value);
            Add(Slug(value));
            Add(Prefix(TrimGemini(Slug(value))));
        }

        if (category.Length > 0)
        {
            var version = VersionPattern().Match(display);

            if (version.Success)
            {
                Add("gemini-" + version.Value + "-" + Slug(category));
                Add("gemini-" + version.Groups[1].Value + "-" + Slug(category));
            }
        }

        var nameSource = display.Length > 0 ? display : category;
        var name       = nameSource.Length == 0
            ? Prefix(modelId.ToLowerInvariant())
            : Prefix(TrimGemini(Slug(nameSource)));

        Add(name);

        return (name, aliases.ToList());
    }

    /// <summary>Resolves a requested public name. Never falls back to a different model.</summary>
    public static GeminiModel Resolve(string? requested, IReadOnlyList<GeminiModel> models)
    {
        if (models.Count == 0) throw new ModelSelectionException("no Gemini models available; refresh the authenticated session");

        requested = (requested ?? "").Trim().ToLowerInvariant();

        if (requested.Length == 0) return models[0];

        var lookup = requested == "gemini-advanced" ? "gemini-pro" : requested;

        // Public canonical names and internal IDs take precedence over potentially shared display
        // aliases, so every unambiguous listed model remains usable.
        var matches = models.Where(m => string.Equals(m.Id, lookup, StringComparison.OrdinalIgnoreCase) || string.Equals(m.ModelId, lookup, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
        {
            matches = models.Where(m => new[] { m.Id, m.ModelId }.Concat(m.Aliases).Any(n => n.ToLowerInvariant() == lookup)).ToList();
        }

        if (matches.Count == 1) return matches[0];

        var available = string.Join(", ", models.Select(m => m.Id));

        if (matches.Count > 1) throw new ModelSelectionException($"ambiguous Gemini model \"{requested}\"; available models: {available}");

        throw new ModelSelectionException($"unsupported Gemini model \"{requested}\"; available models: {available}");
    }

    private static string FirstString(JsonArray values, params int[] indexes)
    {
        foreach (var index in indexes)
        {
            var value = values.At(index).Str();
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return "";
    }

    /// <summary>A JSON integer (not a string, boolean or fractional number).</summary>
    internal static bool TryExactInt(JsonNode? node, out int value)
    {
        value = 0;

        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return false;

        if (v.TryGetValue(out JsonElement element)) return element.TryGetInt32(out value);

        if (v.TryGetValue(out int i)) { value = i; return true; }
        if (v.TryGetValue(out long l) && l is >= int.MinValue and <= int.MaxValue) { value = (int)l; return true; }

        return false;
    }
}
