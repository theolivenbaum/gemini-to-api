using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GeminiToApi.Gemini;

/// <summary>Constants and parsing for Gemini Web's "Create music" tool.</summary>
public static partial class Music
{
    /// <summary>Selects music generation on any model, e.g. <c>gemini-pro-music</c>.</summary>
    public const string MODEL_SUFFIX = "-music";

    public const string LENGTH_SHORT    = "short";
    public const string LENGTH_STANDARD = "standard";
    public const string VOCALS_ON       = "vocals";
    public const string INSTRUMENTAL    = "instrumental";

    /// <summary>[code in inner[0][9][6][2], chip ID in inner[55]].</summary>
    internal static readonly Dictionary<string, (int Code, int Chip)> LengthCodes = new()
    {
        [LENGTH_SHORT]    = (4, 42),
        [LENGTH_STANDARD] = (5, 43),
    };

    internal static readonly Dictionary<string, (int Code, int Chip)> VocalCodes = new()
    {
        [INSTRUMENTAL] = (1, 26),
        [VOCALS_ON]    = (2, 27),
    };

    /// <summary>Genre chips of the music tool and their IDs.</summary>
    public static readonly IReadOnlyDictionary<string, int> Genres = new Dictionary<string, int>
    {
        ["pop"] = 44, ["hip-hop"] = 45, ["rock"] = 46, ["k-pop"] = 40, ["latin"] = 55, ["electronic"] = 47,
        ["r&b"] = 48, ["country"] = 49, ["afrobeats"] = 56, ["reggae"] = 57, ["jazz"] = 50, ["classical"] = 52,
        ["folk"] = 54, ["lo-fi"] = 51, ["acoustic"] = 53, ["cinematic"] = 36, ["ambient"] = 68,
    };

    public static (string Model, bool IsMusic) SplitModel(string? model) => Canvas.SplitSuffix(model, MODEL_SUFFIX);

    public static bool IsMusicModel(string? model) => SplitModel(model).IsMusic;

    /// <summary>Normalizes the options in place and rejects unknown values. Empty means "let Gemini decide".</summary>
    public static void Validate(MusicConfig config)
    {
        config.Length = config.Length.Trim().ToLowerInvariant();
        config.Vocals = config.Vocals.Trim().ToLowerInvariant();
        config.Genre  = config.Genre.Trim().ToLowerInvariant();

        config.Vocals = config.Vocals switch
        {
            "vocals on" or "vocal" or "with vocals" => VOCALS_ON,
            "none" or "no vocals"                   => INSTRUMENTAL,
            "auto" or "custom"                      => "",
            var v                                   => v,
        };

        if (config.Length is "auto" or "custom") config.Length = "";
        if (config.Genre is "auto" or "custom") config.Genre = "";

        if (config.Length.Length > 0 && !LengthCodes.ContainsKey(config.Length))
            throw new MusicValidationException("length must be short or standard");

        if (config.Vocals.Length > 0 && !VocalCodes.ContainsKey(config.Vocals))
            throw new MusicValidationException("vocals must be vocals or instrumental");

        if (config.Genre.Length > 0 && !Genres.ContainsKey(config.Genre))
            throw new MusicValidationException("unsupported genre; use one of: " + string.Join(", ", Genres.Keys));
    }

    /// <summary>
    /// Finds generated audio cards anywhere in a candidate: arrays carrying an "audio/*" MIME type and a
    /// trusted Google media URL. The reply text (candidate[1]) is skipped, so text links never count.
    /// </summary>
    internal static List<GeneratedAudio> ExtractGeneratedAudio(JsonArray candidate)
    {
        var audios = new List<GeneratedAudio>();
        var seen   = new HashSet<string>(StringComparer.Ordinal);

        void Walk(JsonNode? value, int depth)
        {
            if (depth > 12) return;

            switch (value)
            {
                case JsonObject obj:
                    foreach (var (_, child) in obj) Walk(child, depth + 1);
                    break;
                case JsonArray array:
                    if (TryParseAudioItem(array, out var audio))
                    {
                        if (seen.Add(audio.Url)) audios.Add(audio);
                        return;
                    }

                    foreach (var child in array) Walk(child, depth + 1);
                    break;
            }
        }

        for (var i = 0; i < candidate.Count; i++)
        {
            if (i != 1) Walk(candidate[i], 0);
        }

        return audios;
    }

    private static bool TryParseAudioItem(JsonArray item, out GeneratedAudio audio)
    {
        audio = null!;

        var mimeType = item.Select(v => v.Str()).FirstOrDefault(s => s is not null && s.StartsWith("audio/", StringComparison.Ordinal) && !s.Contains(' '));

        if (mimeType is null) return false;

        var urls = new List<string>();

        void Collect(JsonNode? value)
        {
            if (value.Str() is { } s)
            {
                if (MediaUrls.IsTrustedMediaUrl(s)) urls.Add(s);
            }
            else if (value is JsonArray array)
            {
                foreach (var child in array) Collect(child);
            }
        }

        foreach (var value in item) Collect(value);

        if (urls.Count == 0) return false;

        audio = new GeneratedAudio { Url = urls[0], MimeType = mimeType };

        // Prefer the authenticated download URL over thumbnails.
        var download = urls.FirstOrDefault(u => u.Contains("/download", StringComparison.Ordinal) || u.Contains("usercontent.google.com", StringComparison.Ordinal));

        if (download is not null) audio.Url = download;

        audio.FileName = item.Select(v => v.Str()).FirstOrDefault(s => s is not null && Path.HasExtension(s) && !s.Contains('/') && !s.Contains(' '));

        return true;
    }

    /// <summary>Recognizes replies that mean no music is coming.</summary>
    internal static MediaGenerationException? ClassifyReply(string text)
    {
        if (Video.ClassifyReply(text) is { } refusal) return refusal;

        var lower = text.ToLowerInvariant();

        if (lower.Contains("unable to synthesize") || lower.Contains("can't create music") || lower.Contains("cannot create music") || lower.Contains("unable to create music"))
            return new MediaGenerationException(MediaGenerationException.REFUSED, "Gemini did not run its music tool for this request", Video.SafeProviderMessage(text));

        if (lower.Contains("encountering an error") || lower.Contains("something went wrong"))
            return new MediaGenerationException(MediaGenerationException.UPSTREAM, "Gemini Web reported an error while creating music", Video.SafeProviderMessage(text));

        return null;
    }
}

/// <summary>A completed and downloaded music generation.</summary>
public sealed class MusicResult
{
    public GeneratedAudio Audio          { get; set; } = new();
    public byte[]         Data           { get; set; } = [];
    public string         ConversationId { get; set; } = "";
    public string         Model          { get; set; } = "";
    public string         Text           { get; set; } = "";
}
