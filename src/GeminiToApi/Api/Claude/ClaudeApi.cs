using System.Text.Json;
using System.Text.Json.Nodes;
using GeminiToApi.Api.Common;
using GeminiToApi.Gemini;

namespace GeminiToApi.Api.Claude;

/// <summary>Anthropic Messages-compatible routes under <c>/claude/v1</c>.</summary>
public static class ClaudeApi
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/claude/v1");

        group.MapGet("/models", (GeminiClient client) =>
        {
            var data = client.ListModels().Select(Describe).ToList();

            return Results.Json(new JsonObject
            {
                ["data"]     = new JsonArray(data.ToArray<JsonNode?>()),
                ["has_more"] = false,
                ["first_id"] = data.FirstOrDefault()?["id"]?.DeepClone(),
                ["last_id"]  = data.LastOrDefault()?["id"]?.DeepClone(),
            }, ApiHttp.JsonOptions);
        });

        group.MapGet("/models/{modelId}", (string modelId, GeminiClient client) =>
        {
            try
            {
                return Results.Json(Describe(client.ResolveModel(modelId)), ApiHttp.JsonOptions);
            }
            catch (ModelSelectionException e)
            {
                return Error(404, "not_found_error", e.Message);
            }
        });

        group.MapPost("/messages", MessagesAsync);
        group.MapPost("/messages/count_tokens", (Delegate)CountTokensAsync);
    }

    private static JsonObject Describe(ModelInfo model) => new()
    {
        ["id"]           = model.Id,
        ["type"]         = "model",
        ["created_at"]   = DateTimeOffset.FromUnixTimeSeconds(model.Created).ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["display_name"] = model.DisplayName.Length > 0 ? model.DisplayName : model.Id,
    };

    private static IResult Error(int status, string type, string message)
        => Results.Json(new { type = "error", error = new { type, message } }, ApiHttp.JsonOptions, statusCode: status);

    private static IResult ErrorFor(Exception e)
    {
        var status = ApiHttp.StatusFor(e);

        var type = status switch
        {
            404   => "not_found_error",
            429   => "rate_limit_error",
            < 500 => "invalid_request_error",
            _     => "api_error",
        };

        return Error(status, type, e.Message);
    }

    internal sealed record MessageRequest(string Model, List<ChatMessage> Messages, string System, bool Stream, List<ToolBridge.Tool> Tools);

    internal static MessageRequest ParseRequest(JsonElement body)
    {
        List<ChatMessage> messages;

        try
        {
            messages = body.Array("messages").Select(ChatMessages.ParseAnthropic).ToList();
        }
        catch (JsonException e)
        {
            throw new ApiException(400, "Invalid JSON body: " + e.Message);
        }

        var system = body.Prop("system") switch
        {
            { ValueKind: JsonValueKind.String } s => s.GetString()!,
            { ValueKind: JsonValueKind.Array } a  => string.Join("\n", a.EnumerateArray().Where(p => p.Str("type") == "text").Select(p => p.Str("text"))),
            _                                     => "",
        };

        var tools = body.Array("tools")
            .Select(t =>
            {
                var schema = t.Prop("input_schema");
                return new ToolBridge.Tool(t.Str("name"), t.Str("description"), schema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : schema.GetRawText());
            })
            .ToList();

        return new MessageRequest(body.Str("model"), messages, system, body.Bool("stream"), tools);
    }

    internal sealed record MessageResult(string Id, string Model, List<JsonObject> Content, string StopReason, int InputTokens, int OutputTokens, List<CanvasFile>? Canvases);

    internal static async Task<MessageResult> GenerateAsync(MessageRequest request, GeminiClient client, GeminiChat chat, CancellationToken ct)
    {
        ChatMessages.Validate(request.Messages);
        client.ResolveModel(request.Model);

        Func<string, string>? wrap = request.Tools.Count > 0 ? p => ToolBridge.ClaudePrompt(request.Tools, p) : null;

        var sent     = await chat.SendAsync(new GeminiChat.Request(request.Model, request.System, request.Messages, wrap, new GenerateOptions { Model = request.Model }), ct);
        var response = sent.Response;
        var prompt   = sent.Prompt;

        var content    = new List<JsonObject>();
        var stopReason = "end_turn";

        if (response.ReasoningText.Length > 0)
        {
            content.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = response.ReasoningText, ["signature"] = "" });
        }

        if (request.Tools.Count > 0)
        {
            var (uses, text) = ToolBridge.ParseClaude(response.Text);

            if (uses.Count > 0)
            {
                foreach (var use in uses)
                {
                    content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = use.Id, ["name"] = use.Name, ["input"] = JsonNode.Parse(use.Input.GetRawText()) });
                }

                stopReason = "tool_use";
            }
            else
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            }
        }
        else
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = response.Text });
        }

        return new MessageResult("msg_" + Guid.NewGuid().ToString("N"), response.Model, content, stopReason, prompt.Length / 4, response.Text.Length / 4, Canvas.ToFiles(response.Canvases));
    }

    private static JsonObject MessageJson(MessageResult result, bool withContent)
    {
        var message = new JsonObject
        {
            ["id"]            = result.Id,
            ["type"]          = "message",
            ["role"]          = "assistant",
            ["model"]         = result.Model,
            ["content"]       = withContent ? new JsonArray(result.Content.Select(c => (JsonNode?)c.DeepClone()).ToArray()) : new JsonArray(),
            ["stop_reason"]   = withContent ? result.StopReason : null,
            ["stop_sequence"] = null,
            ["usage"]         = new JsonObject { ["input_tokens"] = result.InputTokens, ["output_tokens"] = withContent ? result.OutputTokens : 0 },
        };

        if (result.Canvases is not null) message["canvases"] = JsonSerializer.SerializeToNode(result.Canvases, ApiHttp.JsonOptions);

        return message;
    }

    private static async Task MessagesAsync(HttpContext context, GeminiClient client, GeminiChat chat)
    {
        MessageRequest request;

        try
        {
            request = ParseRequest(await ApiHttp.ReadJsonAsync(context));
            ChatMessages.Validate(request.Messages);
            client.ResolveModel(request.Model);
        }
        catch (Exception e) when (e is ApiException or GeminiException)
        {
            await ErrorFor(e).ExecuteAsync(context);
            return;
        }

        using var deadline = ApiHttp.Deadline(context, TimeSpan.FromMinutes(5));
        var ct = deadline.Token;

        if (!request.Stream)
        {
            try
            {
                var result = await GenerateAsync(request, client, chat, ct);
                await ApiHttp.WriteJsonAsync(context, 200, MessageJson(result, withContent: true));
            }
            catch (Exception e) when (e is ApiException or GeminiException or HttpRequestException)
            {
                await ErrorFor(e).ExecuteAsync(context);
            }

            return;
        }

        ApiHttp.StartEventStream(context);

        try
        {
            var result = await GenerateAsync(request, client, chat, ct);
            await StreamAsync(context, result, ct);
        }
        catch (Exception e) when (e is ApiException or GeminiException or HttpRequestException)
        {
            await ApiHttp.WriteEventAsync(context, "error", new { type = "error", error = new { type = "api_error", message = e.Message } }, ct);
        }
    }

    /// <summary>Replays a finished message as Anthropic stream events.</summary>
    private static async Task StreamAsync(HttpContext context, MessageResult result, CancellationToken ct)
    {
        Task Send(string name, JsonObject payload)
        {
            payload["type"] = name;
            return ApiHttp.WriteEventAsync(context, name, payload, ct);
        }

        await Send("message_start", new JsonObject { ["message"] = MessageJson(result, withContent: false) });

        for (var index = 0; index < result.Content.Count; index++)
        {
            var block = result.Content[index];
            var type  = block["type"]!.GetValue<string>();

            JsonObject start = type switch
            {
                "thinking" => new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" },
                "tool_use" => new JsonObject { ["type"] = "tool_use", ["id"] = block["id"]!.DeepClone(), ["name"] = block["name"]!.DeepClone(), ["input"] = new JsonObject() },
                _          => new JsonObject { ["type"] = "text", ["text"] = "" },
            };

            await Send("content_block_start", new JsonObject { ["index"] = index, ["content_block"] = start });

            switch (type)
            {
                case "thinking":
                case "text":
                    var field     = type == "thinking" ? "thinking" : "text";
                    var deltaType = type == "thinking" ? "thinking_delta" : "text_delta";

                    foreach (var piece in TextUtils.SplitIntoChunks(block[field]!.GetValue<string>()))
                    {
                        await Send("content_block_delta", new JsonObject { ["index"] = index, ["delta"] = new JsonObject { ["type"] = deltaType, [field] = piece } });
                        await Task.Delay(ApiHttp.ChunkDelay, ct);
                    }

                    break;

                case "tool_use":
                    await Send("content_block_delta", new JsonObject
                    {
                        ["index"] = index,
                        ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = block["input"]?.ToJsonString() ?? "{}" },
                    });
                    break;
            }

            await Send("content_block_stop", new JsonObject { ["index"] = index });
        }

        await Send("message_delta", new JsonObject
        {
            ["delta"] = new JsonObject { ["stop_reason"] = result.StopReason, ["stop_sequence"] = null },
            ["usage"] = new JsonObject { ["output_tokens"] = result.OutputTokens },
        });

        await Send("message_stop", new JsonObject());
    }

    private static async Task<IResult> CountTokensAsync(HttpContext context)
    {
        try
        {
            var request = ParseRequest(await ApiHttp.ReadJsonAsync(context));
            var chars   = request.System.Length + request.Messages.Sum(m => m.Content.Length);

            return Results.Json(new { input_tokens = chars / 4 }, ApiHttp.JsonOptions);
        }
        catch (ApiException e)
        {
            return ErrorFor(e);
        }
    }
}
