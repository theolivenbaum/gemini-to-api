using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GeminiToApi.Gemini;

/// <summary>Parsing helpers for Gemini Web's "Videos" tool replies.</summary>
public static partial class Video
{
    /// <summary>One-based JSPB field of candidate[12] that holds the generated-video card.</summary>
    private const int VIDEO_CARD_FIELD = 60;

    [GeneratedRegex(@"https?://googleusercontent\.com/generated_video_content/\d+")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex LinkRegex();

    /// <summary>Reads the structured video card; arbitrary links in the text never count as videos.</summary>
    internal static List<GeneratedVideo> ExtractGeneratedVideos(JsonArray candidate)
    {
        var videos = new List<GeneratedVideo>();
        var card   = candidate.Field(12).Field(VIDEO_CARD_FIELD - 1);

        if (card is null) return videos;

        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Walk(JsonNode? value)
        {
            if (value is not JsonArray item) return;

            if (TryParseVideoItem(item, out var video))
            {
                if (seen.Add(video.Url)) videos.Add(video);
                return;
            }

            foreach (var child in item) Walk(child);
        }

        Walk(card);
        return videos;
    }

    /// <summary>Recognizes [null,2,"video.mp4",…,[thumb,download,…],…,"video/mp4",…,[[s,ns],w,h],…,size].</summary>
    private static bool TryParseVideoItem(JsonArray item, out GeneratedVideo video)
    {
        video = null!;

        if (item.Count < 8) return false;

        var mimeType = item.Select(v => v.Str()).FirstOrDefault(s => s is not null && s.StartsWith("video/", StringComparison.Ordinal));

        if (mimeType is null || item[7] is not JsonArray urls || urls.Count < 2) return false;

        var downloadUrl = urls.At(1).Str();

        if (!MediaUrls.IsTrustedMediaUrl(downloadUrl)) return false;

        video = new GeneratedVideo { Url = downloadUrl!, MimeType = mimeType, FileName = item.At(2).Str() };

        var thumbnail = urls.At(0).Str();

        if (MediaUrls.IsTrustedMediaUrl(thumbnail)) video.ThumbnailUrl = thumbnail;

        for (var i = 8; i < item.Count; i++)
        {
            switch (item[i])
            {
                case JsonArray v when v.Count == 3 && v[0] is JsonArray duration:
                    if (v.At(1).TryInt(out var width) && v.At(2).TryInt(out var height))
                    {
                        video.Width  = width;
                        video.Height = height;
                    }

                    if (duration.Count == 2)
                    {
                        duration.At(0).TryDouble(out var seconds);
                        duration.At(1).TryDouble(out var nanos);
                        video.Duration = seconds + nanos / 1e9;
                    }

                    break;
                case JsonValue number when number.TryDouble(out var size):
                    video.SizeBytes = (long)size;
                    break;
            }
        }

        return true;
    }

    /// <summary>Accepts both "c_…" and ["c_…","r_…"] layouts of payload[1].</summary>
    internal static string ConversationIdFromPayload(JsonArray payload)
    {
        var value = payload.At(1);

        if (value.Str() is { } id) return id;

        if (value is JsonArray ids) return ids.At(0).Str() ?? "";

        return "";
    }

    internal static string StripPlaceholders(string text) => PlaceholderRegex().Replace(text, "").Trim();

    /// <summary>Removes links (which may be signed) and bounds the size.</summary>
    internal static string SafeProviderMessage(string text)
    {
        text = LinkRegex().Replace(text, "").Trim();
        return text.Length > 500 ? new string(text.EnumerateRunes().Take(500).SelectMany(r => r.ToString()).ToArray()) : text;
    }

    /// <summary>Recognizes replies that mean no video is coming.</summary>
    internal static MediaGenerationException? ClassifyReply(string text)
    {
        var lower = text.ToLowerInvariant();

        if (lower.Contains("a lot of requests") || lower.Contains("too many requests") || lower.Contains("try again later") || lower.Contains("try again in"))
            return new MediaGenerationException(MediaGenerationException.QUOTA, "Gemini is temporarily refusing video generation (capacity); retry shortly", SafeProviderMessage(text));

        if (lower.Contains("limit resets") || lower.Contains("reached your limit") || lower.Contains("video limit") || lower.Contains("usage limit"))
            return new MediaGenerationException(MediaGenerationException.QUOTA, "Gemini video generation limit reached; wait for the limit to reset", SafeProviderMessage(text));

        if (lower.Contains("can't generate") || lower.Contains("cannot generate") || lower.Contains("can't create") || lower.Contains("unable to generate") || lower.Contains("can't help with"))
            return new MediaGenerationException(MediaGenerationException.REFUSED, "Gemini declined to generate this video", SafeProviderMessage(text));

        return null;
    }

    /// <summary>
    /// Decodes a batchexecute hNvQHb response. Turns are [[cid,rid], null, userMessage, [candidate…], …]
    /// with the newest first; only the newest turn's candidates and text are returned.
    /// </summary>
    internal static (List<JsonArray> Candidates, string Text) ParseConversationCandidates(string body)
    {
        var records = new List<JsonArray>();

        void Collect(JsonNode? value)
        {
            if (value is not JsonArray a) return;

            if (a.Count > 2 && a.At(0).Str() == "wrb.fr")
            {
                records.Add(a);
                return;
            }

            foreach (var child in a) Collect(child);
        }

        try
        {
            foreach (var value in Jspb.ParseMany(Encoding.UTF8.GetBytes(Jspb.StripXssi(body)))) Collect(value);
        }
        catch (System.Text.Json.JsonException)
        {
            // Like Go's decoder loop: keep whatever was decoded before the malformed value.
        }

        var found = new List<JsonArray>();
        var text  = "";

        foreach (var record in records)
        {
            if (Jspb.TryParse(record.At(2).Str() ?? "") is not JsonArray { Count: > 0 } payload || payload[0] is not JsonArray turns) continue;

            foreach (var rawTurn in turns)
            {
                if (rawTurn is not JsonArray { Count: >= 4 } turn || turn[3] is not JsonArray { Count: > 0 } replies) continue;

                if (replies[0] is JsonArray candidates)
                {
                    foreach (var raw in candidates)
                    {
                        if (raw is not JsonArray candidate) continue;

                        found.Add(candidate);

                        if (text.Length == 0 && candidate.At(1) is JsonArray { Count: > 0 } parts) text = parts.At(0).Str() ?? "";
                    }
                }

                return (found, text);
            }
        }

        return (found, text);
    }

    /// <summary>Checks for the ISO BMFF "ftyp" box at the start of the file.</summary>
    public static bool IsMp4(ReadOnlySpan<byte> data) => data.Length >= 12 && data.Slice(4, 4).SequenceEqual("ftyp"u8);

    /// <summary>Movie duration in seconds from moov/mvhd, or 0.</summary>
    public static double Mp4Duration(ReadOnlySpan<byte> data)
    {
        var mvhd = Mp4Box(Mp4Box(data, "moov"u8), "mvhd"u8);

        if (mvhd.Length < 4) return 0;

        ulong timescale, duration;

        switch (mvhd[0])
        {
            case 0:
                if (mvhd.Length < 20) return 0;
                timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd[12..16]);
                duration  = BinaryPrimitives.ReadUInt32BigEndian(mvhd[16..20]);
                break;
            case 1:
                if (mvhd.Length < 32) return 0;
                timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd[20..24]);
                duration  = BinaryPrimitives.ReadUInt64BigEndian(mvhd[24..32]);
                break;
            default:
                return 0;
        }

        return timescale == 0 ? 0 : (double)duration / timescale;
    }

    private static ReadOnlySpan<byte> Mp4Box(ReadOnlySpan<byte> data, ReadOnlySpan<byte> type)
    {
        while (data.Length >= 8)
        {
            ulong size   = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
            ulong header = 8;

            if (size == 0)
            {
                size = (ulong)data.Length;
            }
            else if (size == 1)
            {
                if (data.Length < 16) return default;
                size   = BinaryPrimitives.ReadUInt64BigEndian(data[8..16]);
                header = 16;
            }

            if (size < header || size > (ulong)data.Length) return default;

            if (data.Slice(4, 4).SequenceEqual(type)) return data[(int)header..(int)size];

            data = data[(int)size..];
        }

        return default;
    }

    /// <summary>Accepts an OpenAI-style size and/or a "16:9" / "9:16" ratio.</summary>
    public static VideoAspectRatio ParseAspect(string? size, string? aspectRatio)
    {
        VideoAspectRatio? fromSize = (size ?? "").Trim().ToLowerInvariant() switch
        {
            ""                                         => null,
            "1280x720" or "1920x1080" or "1792x1024"   => VideoAspectRatio.Landscape,
            "720x1280" or "1080x1920" or "1024x1792"   => VideoAspectRatio.Portrait,
            _                                          => throw new VideoValidationException("size must be 1280x720 or 720x1280"),
        };

        VideoAspectRatio? fromRatio = (aspectRatio ?? "").Trim() switch
        {
            ""     => null,
            "16:9" => VideoAspectRatio.Landscape,
            "9:16" => VideoAspectRatio.Portrait,
            _      => throw new VideoValidationException("aspect ratio must be 16:9 or 9:16"),
        };

        if (fromSize is not null && fromRatio is not null && fromSize != fromRatio)
            throw new VideoValidationException("size and aspect ratio disagree");

        return fromSize == VideoAspectRatio.Portrait || fromRatio == VideoAspectRatio.Portrait ? VideoAspectRatio.Portrait : VideoAspectRatio.Landscape;
    }
}

/// <summary>A completed and downloaded video generation.</summary>
public sealed class VideoResult
{
    public GeneratedVideo Video          { get; set; } = new();
    public byte[]         Data           { get; set; } = [];
    public string         ConversationId { get; set; } = "";
    public string         Model          { get; set; } = "";
    public string         Text           { get; set; } = "";
}
