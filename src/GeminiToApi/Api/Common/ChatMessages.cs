using System.Text;
using System.Text.Json;
using GeminiToApi.Gemini;

namespace GeminiToApi.Api.Common;

/// <summary>Inline binary input extracted from a multimodal API payload (base64 data).</summary>
public sealed record Attachment(string Name, string MimeType, string Data);

/// <summary>A chat message reduced to what Gemini Web takes: text plus attachments.</summary>
public sealed record ChatMessage(string Role, string Content, IReadOnlyList<Attachment> Attachments)
{
    public static ChatMessage Text(string role, string content) => new(role, content, []);
}

/// <summary>Parses OpenAI and Anthropic message shapes (string content, content parts, tool calls/results).</summary>
public static class ChatMessages
{
    private static string Raw(JsonElement e) => e.GetRawText();

    private static bool IsNullish(JsonElement e) => e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    private static string Str(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static JsonElement Prop(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? v : default;

    /// <summary>Common handling of tool results (role "tool") and assistant tool calls; null when neither applies.</summary>
    private static ChatMessage? ParseToolMessage(string role, JsonElement message)
    {
        var content    = Prop(message, "content");
        var toolCallId = Str(message, "tool_call_id");

        if (toolCallId.Length > 0)
        {
            var text = IsNullish(content) ? "" : content.ValueKind == JsonValueKind.String ? content.GetString()! : Raw(content);
            return ChatMessage.Text(role, $"[Tool Result for ID {toolCallId}]: {text}");
        }

        var toolCalls = Prop(message, "tool_calls");

        if (toolCalls.ValueKind == JsonValueKind.Array && toolCalls.GetArrayLength() > 0)
        {
            var parts = new List<string>();

            if (content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } s) parts.Add(s);

            foreach (var call in toolCalls.EnumerateArray())
            {
                if (call.ValueKind != JsonValueKind.Object) continue;

                var function = Prop(call, "function");
                parts.Add($"[Call Tool: {Str(function, "name")} with ID {Str(call, "id")} and Input: {Str(function, "arguments")}]");
            }

            return ChatMessage.Text(role, string.Join("\n", parts));
        }

        return null;
    }

    /// <summary>OpenAI chat message: text parts plus <c>image_url</c> / <c>input_image</c> data URLs.</summary>
    public static ChatMessage ParseOpenAI(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) throw new JsonException("each message must be an object");

        var role = Str(message, "role");

        if (ParseToolMessage(role, message) is { } tool) return tool;

        var content = Prop(message, "content");

        if (IsNullish(content)) return ChatMessage.Text(role, "");

        if (content.ValueKind == JsonValueKind.String) return ChatMessage.Text(role, content.GetString()!);

        if (content.ValueKind != JsonValueKind.Array) throw new JsonException("unsupported messages.content format");

        var texts       = new List<string>();
        var attachments = new List<Attachment>();

        foreach (var part in content.EnumerateArray())
        {
            switch (Str(part, "type").Trim().ToLowerInvariant())
            {
                case "text":
                    var text = Str(part, "text");
                    if (text.Length > 0) texts.Add(text);
                    break;
                case "image_url":
                case "input_image":
                    var imageUrl = Prop(part, "image_url");
                    var url      = imageUrl.ValueKind == JsonValueKind.String ? imageUrl.GetString()! : Str(imageUrl, "url");

                    if (FromDataUrl(url, attachments.Count + 1) is { } attachment) attachments.Add(attachment);
                    break;
            }
        }

        return new ChatMessage(role, string.Join("\n", texts), attachments);
    }

    /// <summary>Anthropic message: text, base64 images, tool_use and tool_result blocks.</summary>
    public static ChatMessage ParseAnthropic(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) throw new JsonException("each message must be an object");

        var role = Str(message, "role");

        if (ParseToolMessage(role, message) is { } tool) return tool;

        var content = Prop(message, "content");

        if (IsNullish(content)) return ChatMessage.Text(role, "");

        if (content.ValueKind == JsonValueKind.String) return ChatMessage.Text(role, content.GetString()!);

        if (content.ValueKind != JsonValueKind.Array) throw new JsonException("unsupported messages.content format");

        var texts       = new List<string>();
        var attachments = new List<Attachment>();
        var index       = 0;

        foreach (var part in content.EnumerateArray())
        {
            index++;

            switch (Str(part, "type").Trim().ToLowerInvariant())
            {
                case "text":
                    var text = Str(part, "text");
                    if (text.Trim().Length > 0) texts.Add(text);
                    break;

                case "image":
                    var source = Prop(part, "source");
                    var data   = Str(source, "data");

                    if (!Str(source, "type").Equals("base64", StringComparison.OrdinalIgnoreCase) || data.Length == 0) break;

                    var mediaType = Str(source, "media_type");
                    attachments.Add(new Attachment($"image_{index}{MediaUrls.ExtensionForMimeType(mediaType)}", mediaType, data));
                    break;

                case "tool_use":
                    var input = Prop(part, "input");
                    texts.Add($"[Call Tool: {Str(part, "name")} with ID {Str(part, "id")} and Input: {(IsNullish(input) ? "" : Raw(input))}]");
                    break;

                case "tool_result":
                    var id     = Str(part, "tool_use_id");
                    var result = Prop(part, "content");

                    string resultText;

                    if (IsNullish(result)) resultText = "";
                    else if (result.ValueKind == JsonValueKind.String) resultText = result.GetString()!;
                    else if (result.ValueKind == JsonValueKind.Array) resultText = string.Join("\n", result.EnumerateArray().Where(p => Str(p, "type") == "text").Select(p => Str(p, "text")));
                    else resultText = Raw(result);

                    texts.Add($"[Tool Result for ID {id}]: {resultText}");
                    break;
            }
        }

        return new ChatMessage(role, string.Join("\n", texts), attachments);
    }

    /// <summary>Only base64 <c>data:</c> URLs are accepted; remote URLs are not downloaded.</summary>
    public static Attachment? FromDataUrl(string value, int index)
    {
        value = value.Trim();

        if (!value.StartsWith("data:", StringComparison.Ordinal)) return null;

        var comma = value.IndexOf(',');

        if (comma < 0) return null;

        var meta = value[5..comma];

        if (!meta.Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;

        var mimeType = meta.EndsWith(";base64", StringComparison.Ordinal) ? meta[..^7] : meta;

        if (mimeType.Length == 0) mimeType = "application/octet-stream";

        return new Attachment($"image_{index}{MediaUrls.ExtensionForMimeType(mimeType)}", mimeType, value[(comma + 1)..]);
    }

    /// <summary>One prompt from a conversation: "System: …", then "User: …" / "Model: …" lines.</summary>
    public static string BuildPrompt(IEnumerable<ChatMessage> messages, string systemPrompt = "")
    {
        var builder = new StringBuilder();

        if (systemPrompt.Length > 0) builder.Append("System: ").Append(systemPrompt).Append("\n\n");

        foreach (var message in messages)
        {
            var role = message.Role.ToLowerInvariant() switch
            {
                "assistant" or "model" => "Model",
                "system"               => "System",
                _                      => "User",
            };

            var content = message.Content;

            if (content.Trim().Length == 0 && message.Attachments.Count > 0) content = $"[{message.Attachments.Count} file(s) attached]";

            builder.Append(role).Append(": ").Append(content).Append('\n');
        }

        return builder.ToString().Trim();
    }

    public static void Validate(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0) throw new ApiException(400, "messages array cannot be empty");

        if (messages.All(m => m.Content.Trim().Length == 0 && m.Attachments.Count == 0)) throw new ApiException(400, "all messages have empty content");
    }

    public static List<InputFile> ToInputFiles(IEnumerable<Attachment> attachments)
    {
        var files = new List<InputFile>();

        foreach (var attachment in attachments)
        {
            if (attachment.Data.Trim().Length == 0) continue;

            if (!TryDecodeBase64(attachment.Data, out var data)) throw new ApiException(400, $"decode attachment \"{attachment.Name}\": invalid base64 data");

            files.Add(new InputFile(attachment.Name, attachment.MimeType, data));
        }

        return files;
    }

    /// <summary>Standard or URL-safe base64, with or without padding.</summary>
    public static bool TryDecodeBase64(string value, out byte[] data)
    {
        var cleaned = value.Trim().Replace("\n", "").Replace("\r", "").Replace('-', '+').Replace('_', '/');

        switch (cleaned.Length % 4)
        {
            case 2: cleaned += "=="; break;
            case 3: cleaned += "="; break;
        }

        try
        {
            data = Convert.FromBase64String(cleaned);
            return true;
        }
        catch (FormatException)
        {
            data = [];
            return false;
        }
    }
}
