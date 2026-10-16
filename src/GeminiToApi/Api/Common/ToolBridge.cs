using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeminiToApi.Api.OpenAI;
using GeminiToApi.Gemini;

namespace GeminiToApi.Api.Common;

/// <summary>
/// Gemini Web has no function calling, so tools are emulated: the prompt lists the tools and asks for a
/// JSON reply, and the reply is parsed back into the vendor's tool-call shape. The prompt texts are
/// ported verbatim from the Go server, one per API surface.
/// </summary>
public static class ToolBridge
{
    public sealed record Tool(string Name, string Description, string ParametersJson);

    private static void AppendTools(StringBuilder b, IEnumerable<Tool> tools, string schemaLabel)
    {
        b.Append("Available tools:\n");

        foreach (var tool in tools)
        {
            b.Append("- name: ").Append(tool.Name);

            if (tool.Description.Length > 0) b.Append(" | description: ").Append(tool.Description);
            if (tool.ParametersJson.Length > 0) b.Append(" | ").Append(schemaLabel).Append(": ").Append(tool.ParametersJson);

            b.Append('\n');
        }
    }

    public static string OpenAIPrompt(IReadOnlyList<Tool> tools, string toolChoiceMode, string forcedTool, string conversation)
    {
        var b = new StringBuilder();

        b.Append("You are an OpenAI-compatible assistant running behind a bridge to Gemini web.\n");
        b.Append("You MUST respond with JSON only. Do not output markdown code fences.\n");
        b.Append("Output schema:\n");
        b.Append("{\"tool_calls\":[{\"name\":\"<tool_name>\",\"arguments\":{}}]} OR {\"content\":\"<assistant_text>\"}\n");
        b.Append("Rules:\n");
        b.Append("- Use only tool names listed below.\n");
        b.Append("- arguments must be valid JSON object.\n");

        if (toolChoiceMode == "required") b.Append("- You must return at least one tool call.\n");
        if (toolChoiceMode == "function" && forcedTool.Length > 0) b.Append("- You must return exactly one tool call with name: ").Append(forcedTool).Append('\n');
        if (toolChoiceMode == "none") b.Append("- Tool calling disabled. Return only {\"content\":\"...\"}.\n");

        AppendTools(b, tools, "parameters");

        b.Append("\nConversation:\n").Append(conversation);
        return b.ToString();
    }

    public static string ClaudePrompt(IReadOnlyList<Tool> tools, string conversation)
    {
        var b = new StringBuilder();

        b.Append("You are a Claude-compatible assistant running behind a bridge that supports tool use.\n");
        b.Append("You MUST respond with JSON only. Do not output markdown code fences.\n");
        b.Append("Output schema:\n");
        b.Append("{\"status\":\"tool_use\",\"tool_calls\":[{\"id\":\"<unique_id>\",\"name\":\"<tool_name>\",\"input\":{}}]} OR {\"status\":\"text\",\"content\":\"<assistant_text>\"}\n");
        b.Append("Rules:\n");
        b.Append("- Use only tool names listed below.\n");
        b.Append("- input must be valid JSON object.\n");

        AppendTools(b, tools, "input_schema");

        b.Append("\nConversation:\n").Append(conversation);
        return b.ToString();
    }

    public static string GeminiPrompt(IReadOnlyList<Tool> tools, string conversation)
    {
        var b = new StringBuilder();

        b.Append("You are a Gemini assistant running behind a bridge that supports function calling.\n");
        b.Append("You MUST respond with JSON only. Do not output markdown code fences.\n");
        b.Append("Output schema:\n");
        b.Append("{\"status\":\"call\",\"tool_calls\":[{\"name\":\"<tool_name>\",\"arguments\":{}}]} OR {\"status\":\"text\",\"content\":\"<assistant_text>\"}\n");
        b.Append("Rules:\n");
        b.Append("- Use only tool names listed below.\n");
        b.Append("- arguments must be valid JSON object.\n");

        AppendTools(b, tools, "parameters");

        b.Append("\nConversation:\n").Append(conversation);
        return b.ToString();
    }

    private static string StringProp(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static IEnumerable<JsonElement> ToolCalls(JsonElement payload)
        => payload.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array ? calls.EnumerateArray() : [];

    /// <summary>OpenAI: keeps only listed (or the forced) tools; returns the calls and any text content.</summary>
    public static (List<ToolCall> Calls, string Content) ParseOpenAI(IReadOnlyList<Tool> tools, string forcedTool, string text)
    {
        if (TextUtils.StripCodeFence(text).Length == 0) return (new List<ToolCall>(), "");

        if (TextUtils.ParseJsonObjectLenient(text) is not { } payload) return (new List<ToolCall>(), text.Trim());

        var allowed = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var calls   = new List<ToolCall>();
        var index   = 0;

        foreach (var call in ToolCalls(payload))
        {
            var name = call.ValueKind == JsonValueKind.Object ? StringProp(call, "name").Trim() : "";

            if (name.Length == 0 || allowed.Count > 0 && !allowed.Contains(name) || forcedTool.Length > 0 && name != forcedTool) { index++; continue; }

            calls.Add(OpenAIApi.NewToolCall(name, NormalizeArguments(call.TryGetProperty("arguments", out var a) ? a : default), index++));
        }

        var content = StringProp(payload, "content").Trim();

        if (content.Length == 0 && calls.Count == 0) content = text.Trim();

        return (calls, content);
    }

    /// <summary>Compact JSON for tool arguments; a JSON-encoded string is unwrapped; anything invalid is "{}".</summary>
    public static string NormalizeArguments(JsonElement raw)
    {
        if (raw.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return "{}";

        var text = raw.ValueKind == JsonValueKind.String ? raw.GetString()!.Trim() : raw.GetRawText();

        if (text.Length == 0) return "{}";

        try
        {
            return JsonNode.Parse(text)?.ToJsonString(Jspb.WriteOptions) ?? "{}";
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    public sealed record ClaudeToolUse(string Id, string Name, JsonElement Input);

    /// <summary>Claude: <c>{"status":"tool_use","tool_calls":[…]}</c> or <c>{"status":"text","content":"…"}</c>.</summary>
    public static (List<ClaudeToolUse> Uses, string Text) ParseClaude(string text)
    {
        if (TextUtils.StripCodeFence(text).Length == 0) return (new List<ClaudeToolUse>(), "");

        if (TextUtils.ParseJsonObjectLenient(text) is not { } payload) return (new List<ClaudeToolUse>(), text);

        var uses = new List<ClaudeToolUse>();

        if (StringProp(payload, "status") == "tool_use")
        {
            foreach (var call in ToolCalls(payload).Where(c => c.ValueKind == JsonValueKind.Object))
            {
                var id    = StringProp(call, "id");
                var input = call.TryGetProperty("input", out var i) && i.ValueKind == JsonValueKind.Object ? i : JsonDocument.Parse("{}").RootElement;

                uses.Add(new ClaudeToolUse(id.Length > 0 ? id : "toolu_" + Guid.NewGuid().ToString("N"), StringProp(call, "name"), input));
            }

            if (uses.Count > 0) return (uses, "");
        }

        return (uses, StringProp(payload, "content"));
    }

    public sealed record GeminiFunctionCall(string Name, JsonElement Args);

    /// <summary>Gemini: <c>{"status":"call","tool_calls":[…]}</c> or <c>{"status":"text","content":"…"}</c>.</summary>
    public static (List<GeminiFunctionCall> Calls, string Text) ParseGemini(string text)
    {
        if (TextUtils.StripCodeFence(text).Length == 0) return (new List<GeminiFunctionCall>(), "");

        if (TextUtils.ParseJsonObjectLenient(text) is not { } payload) return (new List<GeminiFunctionCall>(), text);

        var calls = new List<GeminiFunctionCall>();

        if (StringProp(payload, "status") == "call")
        {
            foreach (var call in ToolCalls(payload).Where(c => c.ValueKind == JsonValueKind.Object))
            {
                calls.Add(new GeminiFunctionCall(StringProp(call, "name"), call.TryGetProperty("arguments", out var a) ? a : default));
            }

            if (calls.Count > 0) return (calls, "");
        }

        return (calls, StringProp(payload, "content"));
    }
}
