using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GeminiToApi.Gemini;

/// <summary>A document or code file Gemini wrote in its Canvas panel.</summary>
public sealed partial class Canvas
{
    public const string DOCUMENT = "document";
    public const string CODE     = "code";

    /// <summary>Selects Canvas mode on any model, e.g. <c>gemini-pro-canvas</c>.</summary>
    public const string MODEL_SUFFIX = "-canvas";

    public string Id       { get; set; } = "";
    public string Title    { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Type     { get; set; } = DOCUMENT;
    public string Language { get; set; } = "";
    public string Content  { get; set; } = "";

    [GeneratedRegex(@"https?://googleusercontent\.com/immersive_entry_chip/(\d+)")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"^```[^\n]*\n(.*?)\n?```$", RegexOptions.Singleline)]
    private static partial Regex FenceRegex();

    private static readonly Dictionary<string, string> MimeTypes = new()
    {
        ["html"] = "text/html", ["css"] = "text/css", ["javascript"] = "text/javascript", ["js"] = "text/javascript",
        ["typescript"] = "text/x-typescript", ["ts"] = "text/x-typescript", ["jsx"] = "text/jsx", ["tsx"] = "text/tsx",
        ["python"] = "text/x-python", ["py"] = "text/x-python", ["java"] = "text/x-java", ["go"] = "text/x-go",
        ["c"] = "text/x-c", ["cpp"] = "text/x-c++", ["csharp"] = "text/x-csharp", ["rust"] = "text/x-rust",
        ["sql"] = "application/sql", ["json"] = "application/json", ["xml"] = "application/xml", ["yaml"] = "application/yaml",
        ["markdown"] = "text/markdown", ["md"] = "text/markdown", ["shell"] = "text/x-shellscript", ["bash"] = "text/x-shellscript",
    };

    /// <summary>Strips the canvas suffix from a requested model name.</summary>
    public static (string Model, bool IsCanvas) SplitModel(string? model) => SplitSuffix(model, MODEL_SUFFIX);

    internal static (string Model, bool Matched) SplitSuffix(string? model, string suffix)
    {
        var trimmed = (model ?? "").Trim();

        if (trimmed.Length > suffix.Length && trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return (trimmed[..^suffix.Length], true);

        return (model ?? "", false);
    }

    /// <summary>
    /// Reads candidate[30]: one entry per canvas, [id, _, title, _, content, …, fileName(9), kind(10), …,
    /// details(14), …]. Code details are [content, language, …]; document details carry the language at 4.
    /// </summary>
    internal static List<Canvas> Extract(JsonArray? candidate)
    {
        var canvases = new List<Canvas>();

        if (candidate is null || candidate.Count <= 30 || candidate[30] is not JsonArray entries) return canvases;

        foreach (var raw in entries)
        {
            if (raw is not JsonArray entry || entry.Count < 11) continue;

            var content = entry.At(4).Str();

            if (string.IsNullOrEmpty(content)) continue;

            var canvas = new Canvas
            {
                Content  = content,
                Id       = entry.At(0).Str() ?? "",
                Title    = entry.At(2).Str() ?? "",
                FileName = entry.At(9).Str() ?? "",
                Type     = entry.At(10).TryDouble(out var kind) && kind == 2 ? CODE : DOCUMENT,
            };

            if (entry.At(14) is JsonArray details)
            {
                foreach (var index in new[] { 1, 4 })
                {
                    var language = details.At(index).Str();

                    if (!string.IsNullOrEmpty(language))
                    {
                        canvas.Language = language;
                        break;
                    }
                }
            }

            if (canvas.Language.Length == 0) canvas.Language = Path.GetExtension(canvas.FileName).TrimStart('.');

            canvases.Add(canvas);
        }

        return canvases;
    }

    /// <summary>The raw file content, without a Markdown code fence.</summary>
    public string Source()
    {
        var content = Content.Trim();
        var match   = FenceRegex().Match(content);
        return match.Success ? match.Groups[1].Value : content;
    }

    /// <summary>Guesses the media type from the language and file name.</summary>
    public string MimeType()
    {
        foreach (var key in new[] { Language.ToLowerInvariant(), Path.GetExtension(FileName).ToLowerInvariant().TrimStart('.') })
        {
            if (MimeTypes.TryGetValue(key, out var mimeType)) return mimeType;
        }

        return Type == DOCUMENT ? "text/markdown" : "text/plain";
    }

    /// <summary>Documents as Markdown, code as a fenced block, like the official APIs return generated files.</summary>
    public string Markdown()
    {
        var content = Content.Trim();

        if (Type != CODE || content.StartsWith("```", StringComparison.Ordinal)) return content;

        return "```" + Language + "\n" + content + "\n```";
    }

    public CanvasFile ToFile() => new()
    {
        Id       = Id,
        Title    = Title.Length > 0 ? Title : null,
        FileName = FileName.Length > 0 ? FileName : Id,
        Type     = Type,
        Language = Language.Length > 0 ? Language : null,
        MimeType = MimeType(),
        Content  = Source(),
    };

    public static List<CanvasFile>? ToFiles(IReadOnlyList<Canvas> canvases)
        => canvases.Count == 0 ? null : canvases.Select(c => c.ToFile()).ToList();

    /// <summary>Replaces Gemini's immersive_entry_chip links with the canvas they stand for; drops dangling ones.</summary>
    internal static string RenderPlaceholders(string text, IReadOnlyList<Canvas> canvases)
    {
        if (!PlaceholderRegex().IsMatch(text)) return text;

        return PlaceholderRegex().Replace(text, match =>
        {
            if (!int.TryParse(match.Groups[1].Value, out var index) || index < 0 || index >= canvases.Count) return "";

            return canvases[index].Markdown();
        });
    }
}

/// <summary>API representation of a canvas. <c>Content</c> is the raw file (code is not fenced).</summary>
public sealed class CanvasFile
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]        public string  Id       { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("title")]     public string? Title    { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("file_name")] public string  FileName { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("type")]      public string  Type     { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("language")]  public string? Language { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("mime_type")] public string  MimeType { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("content")]   public string  Content  { get; set; } = "";
}
