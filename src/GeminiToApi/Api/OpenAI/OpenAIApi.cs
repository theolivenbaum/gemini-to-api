using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GeminiToApi.Api.Common;
using GeminiToApi.Gemini;

namespace GeminiToApi.Api.OpenAI;

/// <summary>OpenAI-compatible routes under <c>/openai/v1</c>.</summary>
public static partial class OpenAIApi
{
    private const string VIDEO_ID_PREFIX        = "video_";
    private const int    EXPECTED_VIDEO_SECONDS = 90;

    [GeneratedRegex(@"(?i)https?://googleusercontent\.com/image_generation_content/\d+")]
    private static partial Regex ImagePlaceholderRegex();

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/openai/v1");

        group.MapGet("/models", (GeminiClient client) => Results.Json(new ModelList { Data = client.ListModels().Select(ModelData.From).ToList() }, ApiHttp.JsonOptions));

        group.MapGet("/models/{model}", (string model, GeminiClient client) =>
        {
            try
            {
                return Results.Json(ModelData.From(client.ResolveModel(model)), ApiHttp.JsonOptions);
            }
            catch (ModelSelectionException e)
            {
                return Error(404, e.Message, "invalid_request_error");
            }
        });

        group.MapPost("/chat/completions", ChatCompletionsAsync);
        group.MapPost("/images/generations", ImageGenerationsAsync);
        group.MapPost("/audio/speech", SpeechAsync);
        group.MapPost("/videos", CreateVideoAsync);
        group.MapGet("/videos", ListVideos);
        group.MapGet("/videos/{id}", GetVideo);
        group.MapDelete("/videos/{id}", DeleteVideo);
        group.MapGet("/videos/{id}/content", VideoContent);
    }

    private static IResult Error(int status, string message, string type)
        => Results.Json(new { error = new { message, type } }, ApiHttp.JsonOptions, statusCode: status);

    private static IResult ErrorFor(Exception e)
    {
        var status = ApiHttp.StatusFor(e);

        if (e is MediaGenerationException media)
        {
            var message = media.ProviderMessage is { Length: > 0 } provider ? $"{media.Message} (Gemini: {provider})" : media.Message;
            return Results.Json(new { error = new { message, type = "api_error", code = media.Code } }, ApiHttp.JsonOptions, statusCode: status);
        }

        var type = status switch
        {
            429         => "rate_limit_error",
            < 500       => "invalid_request_error",
            _           => "api_error",
        };

        return Error(status, e.Message, type);
    }

    // ---------------------------------------------------------------- chat completions

    internal sealed record ChatRequest(
        string                  Model,
        List<ChatMessage>       Messages,
        List<ToolBridge.Tool>   Tools,
        string                  ToolChoiceMode,
        string                  ForcedTool,
        bool                    Stream,
        string                  ReasoningEffort)
    {
        public bool HasToolsEnabled => Tools.Count > 0 && ToolChoiceMode != "none";
    }

    internal static ChatRequest ParseChatRequest(JsonElement body)
    {
        List<ChatMessage> messages;

        try
        {
            messages = body.Array("messages").Select(ChatMessages.ParseOpenAI).ToList();
        }
        catch (JsonException e)
        {
            throw new ApiException(400, "invalid request body: " + e.Message);
        }

        var tools = body.Array("tools")
            .Where(t => t.Str("type").Equals("function", StringComparison.OrdinalIgnoreCase) && t.Prop("function").Str("name").Trim().Length > 0)
            .Select(t =>
            {
                var function   = t.Prop("function");
                var parameters = function.Prop("parameters");
                return new ToolBridge.Tool(function.Str("name").Trim(), function.Str("description").Trim(), parameters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : parameters.GetRawText());
            })
            .ToList();

        var (mode, forced) = ToolChoice(body.Prop("tool_choice"));

        var maxTokens   = body.Number("max_tokens");
        var temperature = body.Number("temperature");

        if (maxTokens < 0) throw new ApiException(400, "max_tokens must be non-negative");
        if (temperature is < 0 or > 2) throw new ApiException(400, "temperature must be between 0 and 2");

        return new ChatRequest(body.Str("model"), messages, tools, mode, forced, body.Bool("stream"), body.Str("reasoning_effort"));
    }

    private static (string Mode, string Forced) ToolChoice(JsonElement choice)
    {
        switch (choice.ValueKind)
        {
            case JsonValueKind.String:
                var mode = choice.GetString()!.Trim().ToLowerInvariant();
                return (mode is "none" or "auto" or "required" ? mode : "auto", "");

            case JsonValueKind.Object:
                var type = choice.Str("type").Trim().ToLowerInvariant();
                var name = choice.Prop("function").Str("name").Trim();

                if (type == "function" && name.Length > 0) return ("function", name);

                return (type is "none" or "auto" or "required" ? type : "auto", "");

            default:
                return ("auto", "");
        }
    }

    /// <summary>Clamps OpenAI reasoning_effort values Gemini Web has no level for onto the nearest one.</summary>
    internal static string ThinkingLevel(string effort) => effort.Trim().ToLowerInvariant() switch
    {
        "none" or "minimal" => "low",
        "xhigh"             => "high",
        _                   => effort,
    };

    private static void Validate(ChatRequest request, GeminiClient client)
    {
        ChatMessages.Validate(request.Messages);

        try
        {
            GeminiProtocol.ParseThinkingLevel(ThinkingLevel(request.ReasoningEffort));
        }
        catch (ThinkingLevelException e)
        {
            throw new ApiException(400, "invalid reasoning_effort: " + e.Message);
        }

        client.ResolveModel(request.Model);
    }

    internal static async Task<ChatCompletionResponse> CreateChatCompletionAsync(ChatRequest request, GeminiClient client, GeminiChat chat, CancellationToken ct)
    {
        Validate(request, client);

        Func<string, string>? wrap = request.HasToolsEnabled ? p => ToolBridge.OpenAIPrompt(request.Tools, request.ToolChoiceMode, request.ForcedTool, p) : null;

        var options = new GenerateOptions
        {
            Model         = request.Model,
            ThinkingLevel = ThinkingLevel(request.ReasoningEffort),
        };

        var sent         = await chat.SendAsync(new GeminiChat.Request(request.Model, "", request.Messages, wrap, options), ct);
        var response     = sent.Response;
        var prompt       = sent.Prompt;
        var message      = new ResponseMessage();
        var finishReason = "stop";

        if (request.HasToolsEnabled)
        {
            var (calls, content) = ToolBridge.ParseOpenAI(request.Tools, request.ForcedTool, response.Text);

            if (calls.Count == 0 && request.ToolChoiceMode is "required" or "function")
            {
                var fallback = request.ForcedTool.Length > 0 ? request.ForcedTool : request.ToolChoiceMode == "required" ? request.Tools.FirstOrDefault()?.Name : null;

                if (fallback is not null) calls.Add(NewToolCall(fallback, "{}", 0));
            }

            if (calls.Count > 0)
            {
                message.ToolCalls = calls;
                finishReason      = "tool_calls";
            }
            else
            {
                message.Content = content;
            }
        }
        else
        {
            var content = ImagePlaceholderRegex().Replace(response.Text, "").Trim();

            if (response.Images.Count > 0)
            {
                if (content.Length > 0) content += "\n\n";
                content += string.Join("\n", response.Images.Select(i => $"![image]({i.Url})"));
            }

            message.Content = content;
        }

        message.ReasoningContent = response.ReasoningText.Length > 0 ? response.ReasoningText : null;
        message.Canvases         = Canvas.ToFiles(response.Canvases);

        var promptTokens     = prompt.Length / 4;
        var completionTokens = response.Text.Length / 4;

        return new ChatCompletionResponse
        {
            Id             = "chatcmpl-" + Guid.NewGuid().ToString("N"),
            Created        = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model          = response.Model,
            RequestedModel = response.RequestedModel.Length > 0 ? response.RequestedModel : null,
            Choices        = [new Choice { Message = message, FinishReason = finishReason }],
            Usage          = new Usage { PromptTokens = promptTokens, CompletionTokens = completionTokens, TotalTokens = promptTokens + completionTokens },
        };
    }

    internal static ToolCall NewToolCall(string name, string arguments, int index) => new()
    {
        Id       = $"call_{Guid.NewGuid():N}_{index}",
        Function = new ToolCallFunction { Name = name, Arguments = arguments },
    };

    private static async Task ChatCompletionsAsync(HttpContext context, GeminiClient client, GeminiChat chat)
    {
        ChatRequest request;

        try
        {
            request = ParseChatRequest(await ApiHttp.ReadJsonAsync(context));
            Validate(request, client);
        }
        catch (Exception e) when (e is ApiException or GeminiException)
        {
            await ErrorFor(e).ExecuteAsync(context);
            return;
        }

        using var deadline = ApiHttp.Deadline(context, TimeSpan.FromMinutes(5));

        if (!request.Stream)
        {
            try
            {
                await ApiHttp.WriteJsonAsync(context, 200, await CreateChatCompletionAsync(request, client, chat, deadline.Token));
            }
            catch (Exception e) when (e is ApiException or GeminiException or HttpRequestException)
            {
                await ErrorFor(e).ExecuteAsync(context);
            }

            return;
        }

        ApiHttp.StartEventStream(context);

        var ct = deadline.Token;

        try
        {
            var response = await CreateChatCompletionAsync(request, client, chat, ct);
            await StreamResponseAsync(context, response, ct);
        }
        catch (Exception e) when (e is ApiException or GeminiException or HttpRequestException)
        {
            var chunk = new ChatCompletionChunk
            {
                Id      = "chatcmpl-err-" + Guid.NewGuid().ToString("N"),
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model   = request.Model,
                Choices = [new ChunkChoice { Delta = new ChunkDelta { Content = "[ERROR] " + e.Message }, FinishReason = "stop" }],
            };

            await ApiHttp.WriteDataAsync(context, ApiHttp.Serialize(chunk), ct);
        }

        await ApiHttp.WriteDataAsync(context, "[DONE]", ct);
    }

    /// <summary>Replays a finished completion as chunks (Gemini Web returns the whole answer at once).</summary>
    private static async Task StreamResponseAsync(HttpContext context, ChatCompletionResponse response, CancellationToken ct)
    {
        var choice = response.Choices[0];

        Task Send(ChunkDelta delta, string? finish = null) => ApiHttp.WriteDataAsync(context, ApiHttp.Serialize(new ChatCompletionChunk
        {
            Id      = response.Id,
            Created = response.Created,
            Model   = response.Model,
            Choices = [new ChunkChoice { Delta = delta, FinishReason = finish }],
        }), ct);

        if (choice.Message.ToolCalls is { Count: > 0 } calls)
        {
            for (var i = 0; i < calls.Count; i++)
            {
                var call = calls[i];

                await Send(new ChunkDelta
                {
                    Role      = i == 0 ? "assistant" : null,
                    ToolCalls = [new ToolCall { Index = i, Id = call.Id, Function = call.Function }],
                });
            }

            await Send(new ChunkDelta(), "tool_calls");
            return;
        }

        var first = true;

        foreach (var piece in TextUtils.SplitIntoChunks(choice.Message.ReasoningContent ?? ""))
        {
            await Send(new ChunkDelta { Role = first ? "assistant" : null, ReasoningContent = piece });
            first = false;
            await Task.Delay(ApiHttp.ChunkDelay, ct);
        }

        foreach (var piece in TextUtils.SplitIntoChunks(choice.Message.Content ?? ""))
        {
            await Send(new ChunkDelta { Role = first ? "assistant" : null, Content = piece });
            first = false;
            await Task.Delay(ApiHttp.ChunkDelay, ct);
        }

        // Canvases are sent whole, once, with the final chunk.
        await Send(new ChunkDelta { Role = first ? "assistant" : null, Canvases = choice.Message.Canvases }, choice.FinishReason);
    }

    // ---------------------------------------------------------------- images

    private static async Task<IResult> ImageGenerationsAsync(HttpContext context, GeminiClient client)
    {
        try
        {
            var body   = await ApiHttp.ReadJsonAsync(context);
            var prompt = body.Str("prompt").Trim();

            if (prompt.Length == 0) throw new ApiException(400, "prompt is required");

            var n = (int)(body.Number("n") ?? 0);

            if (n <= 0) n = 1;
            if (n > 10) throw new ApiException(400, "n must be between 1 and 10");

            var size    = body.Str("size").Trim();
            var wantB64 = body.Str("response_format").Equals("b64_json", StringComparison.OrdinalIgnoreCase);

            var imagePrompt = "Generate an image from this prompt. Return the generated image, not only a text description.\n\nPrompt: " + prompt
                + (size.Length > 0 ? "\nRequested size/aspect: " + size : "");

            using var deadline = ApiHttp.Deadline(context, TimeSpan.FromMinutes(10));

            var data = new List<ImageData>();

            while (data.Count < n)
            {
                var response  = await client.GenerateContentAsync(imagePrompt, new GenerateOptions { Model = body.Str("model"), DownloadGeneratedImages = wantB64 }, deadline.Token);
                var generated = response.Images.Where(i => i.Generated).ToList();

                if (generated.Count == 0) throw new GeminiException("provider returned no generated images");

                foreach (var image in generated.Take(n - data.Count))
                {
                    if (wantB64 && string.IsNullOrEmpty(image.B64Json)) throw new GeminiException("provider returned image metadata without image bytes");

                    data.Add(new ImageData { RevisedPrompt = prompt, B64Json = wantB64 ? image.B64Json : null, Url = wantB64 ? null : image.Url });
                }
            }

            return Results.Json(new ImageGenerationResponse { Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Data = data }, ApiHttp.JsonOptions);
        }
        catch (Exception e) when (e is ApiException or GeminiException or HttpRequestException)
        {
            return ErrorFor(e);
        }
    }

    // ---------------------------------------------------------------- audio/speech (music)

    /// <summary>
    /// Gemini Web has no text-to-speech, so this generates music from <c>input</c>: voice "instrumental"
    /// or "vocals" picks the vocal mode, instructions are appended as style guidance, and the
    /// <c>length</c> / <c>genre</c> extensions pick the web client's options.
    /// </summary>
    private static async Task<IResult> SpeechAsync(HttpContext context, GeminiClient client)
    {
        try
        {
            var body = await ApiHttp.ReadJsonAsync(context);

            if (body.Str("stream_format") == "sse") throw new ApiException(400, "stream_format sse is not supported; music is returned as one audio file");

            var prompt       = body.Str("input").Trim();
            var instructions = body.Str("instructions").Trim();

            if (instructions.Length > 0 && prompt.Length > 0) prompt += "\n\nStyle: " + instructions;

            var voiceElement = body.Prop("voice");
            var voice        = voiceElement.ValueKind == JsonValueKind.String ? voiceElement.GetString()! : voiceElement.Str("id");

            var config = new MusicConfig { Length = body.Str("length"), Genre = body.Str("genre") };

            if (voice.ToLowerInvariant() is Music.INSTRUMENTAL or Music.VOCALS_ON) config.Vocals = voice.ToLowerInvariant();

            // OpenAI speech model names (tts-1, gpt-4o-mini-tts, …) use the default model.
            var model = body.Str("model");
            var lower = model.ToLowerInvariant();

            if (lower.StartsWith("tts", StringComparison.Ordinal) || lower.Contains("-tts", StringComparison.Ordinal)) model = "";

            using var deadline = ApiHttp.Deadline(context, TimeSpan.FromMinutes(10));

            var result = await client.GenerateMusicAsync(prompt, model, config, deadline.Token);

            return Results.Bytes(result.Data, string.IsNullOrEmpty(result.Audio.MimeType) ? "audio/mpeg" : result.Audio.MimeType);
        }
        catch (Exception e) when (e is ApiException or GeminiException or HttpRequestException)
        {
            return ErrorFor(e);
        }
    }

    // ---------------------------------------------------------------- videos

    private static async Task<IResult> CreateVideoAsync(HttpContext context, GeminiClient client)
    {
        try
        {
            string prompt, model, size, aspectRatio;

            if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);

                if (form.Files.GetFiles("input_reference").Count > 0)
                    throw new ApiException(400, "input_reference is not supported; only text-to-video is available");

                prompt      = form["prompt"].ToString();
                model       = form["model"].ToString();
                size        = form["size"].ToString();
                aspectRatio = form["aspect_ratio"].ToString();
            }
            else
            {
                var body    = await ApiHttp.ReadJsonAsync(context);
                prompt      = body.Str("prompt");
                model       = body.Str("model");
                size        = body.Str("size");
                aspectRatio = body.Str("aspect_ratio");
            }

            var job = client.StartVideoJob(prompt, model, Video.ParseAspect(size, aspectRatio));

            return Results.Json(ToVideoObject(job), ApiHttp.JsonOptions);
        }
        catch (Exception e) when (e is ApiException or GeminiException)
        {
            return ErrorFor(e);
        }
    }

    private static IResult ListVideos(GeminiClient client)
    {
        var list = new VideoList { Data = client.VideoJobs.List().Select(ToVideoObject).ToList() };

        if (list.Data.Count > 0)
        {
            list.FirstId = list.Data[0].Id;
            list.LastId  = list.Data[^1].Id;
        }

        return Results.Json(list, ApiHttp.JsonOptions);
    }

    private static string JobId(string id) => id.StartsWith(VIDEO_ID_PREFIX, StringComparison.Ordinal) ? id[VIDEO_ID_PREFIX.Length..] : id;

    private static IResult GetVideo(string id, GeminiClient client)
    {
        try
        {
            return Results.Json(ToVideoObject(client.VideoJobs.Get(JobId(id))), ApiHttp.JsonOptions);
        }
        catch (GeminiException e)
        {
            return ErrorFor(e);
        }
    }

    private static IResult DeleteVideo(string id, GeminiClient client)
    {
        try
        {
            client.VideoJobs.Delete(JobId(id));
            return Results.Json(new VideoDeleted { Id = id }, ApiHttp.JsonOptions);
        }
        catch (GeminiException e)
        {
            return ErrorFor(e);
        }
    }

    private static IResult VideoContent(string id, HttpContext context, GeminiClient client)
    {
        var variant = context.Request.Query["variant"].ToString();

        if (variant.Length > 0 && variant != "video") return Error(400, $"variant \"{variant}\" is not available; only video is supported", "invalid_request_error");

        try
        {
            var (job, data) = client.VideoJobs.Content(JobId(id));
            return Results.File(data, string.IsNullOrEmpty(job.Video.MimeType) ? "video/mp4" : job.Video.MimeType, id + ".mp4");
        }
        catch (GeminiException e)
        {
            return ErrorFor(e);
        }
    }

    internal static VideoObject ToVideoObject(VideoJob job)
    {
        var id    = VIDEO_ID_PREFIX + job.Id;
        var video = new VideoObject
        {
            Id             = id,
            Model          = job.Model,
            Status         = job.Status,
            CreatedAt      = job.CreatedAt.ToUnixTimeSeconds(),
            Prompt         = job.Prompt,
            Size           = job.Size,
            ConversationId = job.ConversationId.Length > 0 ? job.ConversationId : null,
            Message        = job.Message.Length > 0 ? job.Message : null,
        };

        if (job.Video.Duration > 0) video.Seconds = ((int)Math.Round(job.Video.Duration)).ToString();

        switch (job.Status)
        {
            case VideoJobStatus.IN_PROGRESS:
                video.Progress = (int)Math.Min(95, (DateTimeOffset.UtcNow - job.CreatedAt).TotalSeconds * 100 / EXPECTED_VIDEO_SECONDS);
                break;
            case VideoJobStatus.COMPLETED:
                video.Progress   = 100;
                video.ContentUrl = "/openai/v1/videos/" + id + "/content";
                break;
        }

        if (job.CompletedAt is { } completed)
        {
            video.CompletedAt = completed.ToUnixTimeSeconds();
            video.ExpiresAt   = job.ExpiresAt?.ToUnixTimeSeconds();
        }

        if (job.Error is { } error) video.Error = new VideoJobError { Code = error.Code, Message = error.Message };

        return video;
    }
}
