using System.Text.Json;

namespace GeminiToApi.Gemini;

/// <summary>Text helpers shared by the client and the API surfaces (Go: internal/commons/utils).</summary>
public static class TextUtils
{
    /// <summary>
    /// Separates the thinking section (between <c>&lt;ctrl94&gt;thought</c> and <c>&lt;ctrl95&gt;</c>) from the
    /// answer. Returns (thinking, text).
    /// </summary>
    public static (string Thinking, string Text) ExtractThinkingAndText(string raw)
    {
        var startIdx = -1;
        var startLen = 0;

        foreach (var marker in new[] { "<ctrl94>thought", "<ctrl94>" })
        {
            var idx = raw.IndexOf(marker, StringComparison.Ordinal);

            if (idx >= 0)
            {
                startIdx = idx;
                startLen = marker.Length;
                break;
            }
        }

        if (startIdx < 0) return ("", raw);

        var endIdx = raw.IndexOf("<ctrl95>", startIdx + startLen, StringComparison.Ordinal);

        if (endIdx < 0) return (raw[(startIdx + startLen)..].Trim(), raw[..startIdx].Trim());

        var thinking = raw[(startIdx + startLen)..endIdx].Trim();
        var text     = (raw[..startIdx] + raw[(endIdx + "<ctrl95>".Length)..]).Trim();

        return (thinking, text);
    }

    /// <summary>
    /// Splits a finished answer into small chunks for simulated streaming, preserving every character
    /// (string.Concat(chunks) == text). Boundaries prefer whitespace near 8 characters.
    /// </summary>
    public static List<string> SplitIntoChunks(string text)
    {
        var chunks = new List<string>();

        if (string.IsNullOrEmpty(text)) return chunks;

        var runes = text.EnumerateRunes().ToArray();
        var start = 0;

        static bool IsSpace(System.Text.Rune r) => r.Value is ' ' or '\n' or '\t';

        while (start < runes.Length)
        {
            var end = start + 8;

            if (end >= runes.Length)
            {
                chunks.Add(Concat(runes, start, runes.Length));
                break;
            }

            var boundary = -1;

            for (var i = end; i < runes.Length && i <= end + 10; i++)
            {
                if (IsSpace(runes[i])) { boundary = i + 1; break; }
            }

            if (boundary == -1)
            {
                for (var i = end; i > start; i--)
                {
                    if (IsSpace(runes[i])) { boundary = i + 1; break; }
                }
            }

            if (boundary <= start || boundary > runes.Length) boundary = end;

            chunks.Add(Concat(runes, start, boundary));
            start = boundary;
        }

        return chunks;
    }

    private static string Concat(System.Text.Rune[] runes, int start, int end)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = start; i < end; i++) builder.Append(runes[i].ToString());
        return builder.ToString();
    }

    /// <summary>The first balanced top-level JSON object in the text, or "".</summary>
    public static string ExtractFirstJsonObject(string text)
    {
        var start = text.IndexOf('{');

        if (start < 0) return "";

        var depth    = 0;
        var inString = false;
        var escaped  = false;

        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }

            switch (ch)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return text[start..(i + 1)];
                    break;
            }
        }

        return "";
    }

    /// <summary>Removes a Markdown code fence (```json …```) around a string.</summary>
    public static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();

        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;

        trimmed = trimmed[3..];

        if (trimmed.StartsWith("json", StringComparison.Ordinal)) trimmed = trimmed[4..];
        else if (trimmed.StartsWith("JSON", StringComparison.Ordinal)) trimmed = trimmed[4..];

        trimmed = trimmed.Trim();

        var idx = trimmed.LastIndexOf("```", StringComparison.Ordinal);

        if (idx >= 0) trimmed = trimmed[..idx].Trim();

        return trimmed;
    }

    /// <summary>Parses text as a JSON object, falling back to the first embedded object. Null when neither parses.</summary>
    public static JsonElement? ParseJsonObjectLenient(string text)
    {
        var cleaned = StripCodeFence(text);

        if (cleaned.Length == 0) return null;

        foreach (var candidate in new[] { cleaned, ExtractFirstJsonObject(text) })
        {
            if (candidate.Length == 0) continue;

            try
            {
                using var doc = JsonDocument.Parse(candidate);

                if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Model output that is not JSON: try the next candidate.
            }
        }

        return null;
    }
}
