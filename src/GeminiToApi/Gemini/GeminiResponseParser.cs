using System.Text.Json.Nodes;

namespace GeminiToApi.Gemini;

/// <summary>Parses the chunked StreamGenerate reply (one JSON array per line after the XSSI guard).</summary>
internal static class GeminiResponseParser
{
    public static GeminiResponse Parse(string text)
    {
        string? finalText = null;
        var     found     = false;

        var images         = new Dictionary<string, GeneratedImage>(StringComparer.Ordinal);
        var imageOrder     = new List<string>();
        var videos         = new List<GeneratedVideo>();
        var seenVideos     = new HashSet<string>(StringComparer.Ordinal);
        var audios         = new List<GeneratedAudio>();
        var seenAudios     = new HashSet<string>(StringComparer.Ordinal);
        var canvases       = new List<Canvas>();
        var conversationId = "";
        var choiceId       = "";
        var metaCid        = "";

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.StartsWith(")]}'", StringComparison.Ordinal)) line = line[4..];

            if (line.Length == 0 || line[0] != '[') continue;

            if (Jspb.TryParse(line) is not JsonArray root) continue;

            foreach (var item in root)
            {
                if (item is not JsonArray itemArray || itemArray.Count < 1) continue;

                if (ExtractBardError(itemArray) is { } error) throw new GeminiException(error);

                if (itemArray.Count < 3 || itemArray[2].Str() is not { } payloadText) continue;

                if (Jspb.TryParse(payloadText) is not JsonArray payload) continue;

                MediaUrls.CollectImages(payload, images, imageOrder);

                var id = Video.ConversationIdFromPayload(payload);

                if (id.Length > 0) conversationId = id;

                // Newer replies (e.g. the Videos tool acknowledgement) stream a turn as numbered-field
                // updates, [null,["c_…","r_…"],{"7":["text"],"44":true}], instead of candidates at index 4.
                if (payload.Count < 5 && payload.At(2) is JsonObject fields && fields["7"] is JsonArray { Count: > 0 } parts && parts[0].Str() is { Length: > 0 } reply)
                {
                    finalText = reply;
                    found     = true;
                }

                if (payload.Count > 4 && payload[4] is JsonArray { Count: > 0 } candidates)
                {
                    foreach (var rawCandidate in candidates)
                    {
                        if (rawCandidate is not JsonArray candidate) continue;

                        foreach (var image in MediaUrls.ExtractGeneratedImages(candidate))
                        {
                            if (!images.ContainsKey(image.Url)) imageOrder.Add(image.Url);
                            images[image.Url] = image;
                        }

                        foreach (var audio in Music.ExtractGeneratedAudio(candidate))
                        {
                            if (seenAudios.Add(audio.Url)) audios.Add(audio);
                        }

                        foreach (var video in Video.ExtractGeneratedVideos(candidate))
                        {
                            if (seenVideos.Add(video.Url)) videos.Add(video);
                        }
                    }

                    var first = candidates[0] as JsonArray;

                    // Canvas content is streamed in some frames only; keep the latest.
                    var frameCanvases = Canvas.Extract(first);

                    if (frameCanvases.Count > 0) canvases = frameCanvases;

                    if (first is { Count: >= 2 } && first[1] is JsonArray { Count: > 0 } contentParts && contentParts[0].Str() is { } resText)
                    {
                        finalText = resText;
                        choiceId  = first.At(0).Str() ?? "";
                        metaCid   = payload.At(1).Str() ?? "";
                        found     = true;
                    }
                }
            }
        }

        if (found || images.Count > 0 || videos.Count > 0 || audios.Count > 0)
        {
            var rendered        = Canvas.RenderPlaceholders(finalText ?? "", canvases);
            var (reasoning, clean) = TextUtils.ExtractThinkingAndText(rendered);

            return new GeminiResponse
            {
                Text           = clean,
                ReasoningText  = reasoning,
                Images         = imageOrder.Select(url => images[url]).ToList(),
                Videos         = videos,
                Canvases       = canvases,
                Audios         = audios,
                ConversationId = conversationId.Length > 0 ? conversationId : metaCid,
                ChoiceId       = choiceId,
            };
        }

        var sample = text.Length > 500 ? text[..500] : text;
        throw new GeminiException("failed to parse response. Sample: " + sample);
    }

    /// <summary>Finds a BardErrorInfo block and reports the numeric codes next to it.</summary>
    internal static string? ExtractBardError(JsonArray item)
    {
        foreach (var element in item)
        {
            var codes = new List<int>();

            if (Walk(element, codes))
            {
                return codes.Count > 0
                    ? $"Google Gemini Web returned an error (BardErrorInfo code [{string.Join(' ', codes)}]). This usually indicates session expiration, rate limits, context window limits, or bot protection/CAPTCHA block."
                    : "Google Gemini Web returned a BardErrorInfo block.";
            }
        }

        return null;

        static bool Walk(JsonNode? value, List<int> codes)
        {
            switch (value)
            {
                case JsonArray array:
                    var hasError = false;
                    foreach (var child in array) hasError |= Walk(child, codes);
                    return hasError;
                case JsonValue v when v.Str() is { } s:
                    return s.Contains("BardErrorInfo", StringComparison.Ordinal);
                case JsonValue v when v.TryInt(out var code):
                    codes.Add(code);
                    return false;
                default:
                    return false;
            }
        }
    }
}
