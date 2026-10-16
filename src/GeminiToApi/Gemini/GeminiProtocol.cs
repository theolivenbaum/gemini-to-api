using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace GeminiToApi.Gemini;

/// <summary>
/// Endpoints, headers and payload builders of the Gemini Web protocol. Every index and constant below
/// was taken from browser captures (see <c>.reference/internal/modules/providers</c>); keep them as is.
/// </summary>
internal static class GeminiProtocol
{
    public const string ENDPOINT_GOOGLE         = "https://www.google.com/";
    public const string ENDPOINT_INIT           = "https://gemini.google.com/app";
    public const string ENDPOINT_GENERATE       = "https://gemini.google.com/_/BardChatUi/data/assistant.lamda.BardFrontendService/StreamGenerate";
    public const string ENDPOINT_ROTATE_COOKIES = "https://accounts.google.com/RotateCookies";
    public const string ENDPOINT_UPLOAD         = "https://content-push.googleapis.com/upload";
    public const string ENDPOINT_BATCH_EXEC     = "https://gemini.google.com/_/BardChatUi/data/batchexecute";

    public const string USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    public const string MODEL_HEADER_KEY       = "x-goog-ext-525001261-jspb";
    public const string DEFAULT_PUSH_ID        = "feeds/mcudyrk2a4khkz";
    public const string READ_CONVERSATION_RPC  = "hNvQHb";
    public const string FORM_CONTENT_TYPE      = "application/x-www-form-urlencoded;charset=utf-8";

    /// <summary>Payload length sent by the current web client when a tool is selected.</summary>
    public const int WEB_CLIENT_INNER_LENGTH = 99;

    public const int CANVAS_TOOL_MODE = 2;
    public const int VIDEO_TOOL_MODE  = 11;
    public const int MUSIC_TOOL_MODE  = 21;

    public static readonly IReadOnlyDictionary<string, string> DefaultHeaders = new Dictionary<string, string>
    {
        ["Origin"]        = "https://gemini.google.com",
        ["Referer"]       = "https://gemini.google.com/",
        ["User-Agent"]    = USER_AGENT,
        ["X-Same-Domain"] = "1",
    };

    public static string AccountUrl(string endpoint, string authUser)
        => authUser.Length == 0 ? endpoint : ReplaceFirst(endpoint, "https://gemini.google.com/", "https://gemini.google.com/u/" + authUser + "/");

    public static string SourcePath(string authUser) => authUser.Length == 0 ? "/app" : "/u/" + authUser + "/app";

    private static string ReplaceFirst(string text, string search, string replacement)
    {
        var index = text.IndexOf(search, StringComparison.Ordinal);
        return index < 0 ? text : text[..index] + replacement + text[(index + search.Length)..];
    }

    /// <summary>Maps effort names to the Gemini Web header and body enum.</summary>
    public static int ParseThinkingLevel(string? level)
    {
        switch ((level ?? "").Trim().ToLowerInvariant())
        {
            case "":
            case "low":    return 1;
            case "medium": return 4;
            case "high":   return 2;
            default:       throw new ThinkingLevelException($"unsupported thinking level \"{level}\": use low, medium, or high");
        }
    }

    /// <summary>Uses the routing identity discovered for this account; index 4 is a model ID, not a trace ID.</summary>
    public static Dictionary<string, string> BuildModelHeaders(GeminiModel model, string requestId, string generationId, int thinkingLevel, bool webTool = false)
    {
        if (model.ModelId.Length == 0 || model.ModelNumber <= 0 || model.Capacity <= 0 || model.CapacityField is not (12 or 13))
            throw new GeminiException($"model '{model.Id}' has incomplete Gemini routing information");

        var offset = model.CapacityField - 12;
        var header = Jspb.NewArray(17 + offset);

        header[0]                       = 1;
        header[4]                       = model.ModelId;
        header[7]                       = 0;
        header[8]                       = webTool ? Jspb.Ints(4, 5, 6, 8, 16) : Jspb.Ints(4, 5, 6, 8);
        header[model.CapacityField - 1] = model.Capacity; // CapacityField is a one-based protocol field number
        header[14 + offset]             = model.ModelNumber;
        header[15 + offset]             = thinkingLevel;
        header[16 + offset]             = generationId;

        return new Dictionary<string, string>
        {
            [MODEL_HEADER_KEY]           = Jspb.Serialize(header),
            ["x-goog-ext-525005358-jspb"] = Jspb.Serialize(Jspb.Of(requestId, 1)),
            ["x-goog-ext-73010989-jspb"]  = "[0]",
            ["x-goog-ext-73010990-jspb"]  = "[0,0,0]",
        };
    }

    /// <summary>The model-less selector header the batchexecute RPCs send.</summary>
    public static string BuildBatchHeader(string generationId, bool webTool)
    {
        var header = Jspb.NewArray(17);
        header[0]  = 1;
        header[8]  = webTool ? Jspb.Ints(4, 5, 6, 8, 16) : Jspb.Ints(4, 5, 6, 8);
        header[16] = generationId;
        return Jspb.Serialize(header);
    }

    public sealed record UploadedFile(string Id, string Name);

    public static JsonArray BuildGenerateInner(string prompt, IReadOnlyList<UploadedFile> files, int modelNumber, string language, string requestId, bool temporary)
    {
        JsonArray message;

        if (files.Count == 0)
        {
            message = Jspb.Of(prompt);
        }
        else
        {
            var fileData = new JsonArray();

            foreach (var file in files) fileData.Add(Jspb.Of(Jspb.Of(file.Id), file.Name));

            message = Jspb.Of(prompt, 0, null, fileData, null, null, 0);
        }

        var inner = Jspb.NewArray(81);

        inner[0]  = message;
        inner[1]  = Jspb.Of(language);
        inner[2]  = Jspb.Of("", "", "", null, null, null, null, null, null, "");
        inner[6]  = Jspb.Ints(1);
        inner[7]  = 1;
        inner[10] = 1;
        inner[11] = 0;
        inner[17] = Jspb.Of(Jspb.Ints(0));
        inner[18] = 0;
        inner[27] = 1;
        inner[30] = Jspb.Ints(4);
        inner[41] = Jspb.Ints(1);
        inner[53] = 0;
        inner[59] = requestId;
        inner[61] = new JsonArray();
        inner[68] = 1;
        inner[79] = modelNumber;
        inner[80] = 1;

        if (temporary) inner[45] = 1;

        return inner;
    }

    /// <summary>
    /// Points the request at an existing conversation: inner[2] is [conversation id, response id,
    /// choice id, …], the same metadata the web client sends for a follow-up message.
    /// </summary>
    public static void ApplyConversation(JsonArray inner, ConversationRef conversation)
    {
        var metadata = inner[2] as JsonArray ?? Jspb.Of("", "", "", null, null, null, null, null, null, "");

        metadata[0] = conversation.ConversationId;
        metadata[1] = conversation.ResponseId;
        metadata[2] = conversation.ChoiceId;

        if (inner[2] is null) inner[2] = metadata;
    }

    /// <summary>Extends a StreamGenerate payload to the current web client's layout; tool modes go on top.</summary>
    public static void ApplyWebClientInner(JsonArray inner)
    {
        Jspb.EnsureLength(inner, WEB_CLIENT_INNER_LENGTH);

        inner[30] = Jspb.Ints(4, 16);
        inner[68] = 2;
        inner[91] = 0;
        inner[96] = 0;
        inner[98] = 1;
    }

    public static void ApplyCanvasInner(JsonArray inner)
    {
        ApplyWebClientInner(inner);
        inner[49] = CANVAS_TOOL_MODE;
        inner[67] = 0;
    }

    /// <summary>The message part padded to 10 fields, the shape tools put their options in (index 9).</summary>
    private static JsonArray ToolMessage(JsonArray inner)
    {
        var message = inner[0] as JsonArray ?? new JsonArray();

        if (message.Count < 10)
        {
            var hadSecond = message.Count >= 2;

            Jspb.EnsureLength(message, 10);

            if (!hadSecond) message[1] = 0;

            message[6] = 0;
        }

        return message;
    }

    /// <summary>Turns a normal StreamGenerate payload into what the web client sends from the /videos page.</summary>
    public static void ApplyVideoInner(JsonArray inner, VideoAspectRatio aspect)
    {
        ApplyWebClientInner(inner);

        var message = ToolMessage(inner);
        var options = Jspb.NewArray(7);

        options[6] = Jspb.Of(Jspb.Of(null, null, null, (int)aspect));
        message[9] = options;

        inner[45] = null; // completion is read back from the saved conversation
        inner[49] = VIDEO_TOOL_MODE;
        inner[55] = Jspb.Of(Jspb.Ints(aspect == VideoAspectRatio.Portrait ? 17 : 16));
    }

    /// <summary>
    /// Selects the music tool and its options: inner[0][9][6] = [null, null, [lengthCode, vocalsCode]]
    /// and inner[55] = [[chip IDs]].
    /// </summary>
    public static void ApplyMusicInner(JsonArray inner, MusicConfig config)
    {
        ApplyWebClientInner(inner);

        inner[45] = null; // a failed stream is recovered from the saved conversation
        inner[49] = MUSIC_TOOL_MODE;
        inner[67] = 0;

        var codes = new List<JsonNode?>();
        var chips = new List<JsonNode?>();

        if (Music.LengthCodes.TryGetValue(config.Length, out var length))
        {
            codes.Add(length.Code);
            chips.Add(length.Chip);
        }

        if (Music.VocalCodes.TryGetValue(config.Vocals, out var vocals))
        {
            if (codes.Count == 0) codes.Add(null);

            codes.Add(vocals.Code);
            chips.Add(vocals.Chip);
        }

        if (Music.Genres.TryGetValue(config.Genre, out var genre)) chips.Add(genre);

        if (chips.Count == 0) return;

        var message = ToolMessage(inner);
        var options = Jspb.NewArray(7);

        options[6] = codes.Count > 0 ? Jspb.Of(null, null, new JsonArray(codes.ToArray())) : new JsonArray();
        message[9] = options;

        inner[55] = Jspb.Of(new JsonArray(chips.ToArray()));
    }

    /// <summary>Encodes a form body like Go's url.Values.Encode (keys sorted, spaces as '+').</summary>
    public static string FormEncode(IEnumerable<KeyValuePair<string, string>> values)
    {
        var builder = new StringBuilder();

        foreach (var (key, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (builder.Length > 0) builder.Append('&');

            builder.Append(WebUtility.UrlEncode(key)).Append('=').Append(WebUtility.UrlEncode(value));
        }

        return builder.ToString();
    }

    public static string RandomRequestId() => Random.Shared.Next(10000, 100000).ToString();
}
