using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GeminiToApi.Api.Common;
using GeminiToApi.Gemini;

namespace GeminiToApi.Api.Gemini;

/// <summary>Google Generative Language API-compatible routes under <c>/gemini/v1beta</c>.</summary>
public static partial class GeminiApi
{
    /// <summary>
    /// The job ID in a <c>files/{id}:download</c> path. The last match is used because google-genai, given a
    /// non-https video URI, appends the whole URI to the path.
    /// </summary>
    [GeneratedRegex("files/([a-z0-9]+):download")]
    private static partial Regex VideoFileIdRegex();

    private static readonly string[] GenerationMethods = ["generateContent", "streamGenerateContent"];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/gemini/v1beta");

        group.MapGet("/models", (GeminiClient client) =>
            Results.Json(new JsonObject { ["models"] = new JsonArray(client.ListModels().Select(m => (JsonNode?)Describe(m)).ToArray()) }, ApiHttp.JsonOptions));

        group.MapGet("/models/{model}", (string model, GeminiClient client) =>
        {
            try
            {
                return Results.Json(Describe(client.ResolveModel(model)), ApiHttp.JsonOptions);
            }
            catch (ModelSelectionException e)
            {
                return Error(404, e.Message, "invalid_request_error");
            }
        });

        group.MapPost("/models/{spec}", ModelActionAsync);
        group.MapGet("/models/{model}/operations/{operation}", GetOperation);
        group.MapGet("/files/{**path}", DownloadFile);

        group.MapPost("/deepresearch", DeepResearchAsync);
        group.MapPost("/deepresearch/stream", DeepResearchStreamAsync);
        group.MapPost("/interactions", InteractionCreateAsync);
        group.MapGet("/interactions/{id}", InteractionGet);
    }

    private static JsonObject Describe(ModelInfo model) => new()
    {
        ["name"]                       = "models/" + model.Id,
        ["displayName"]                = model.DisplayName,
        ["supportedGenerationMethods"] = new JsonArray(GenerationMethods.Select(m => (JsonNode?)m).ToArray()),
    };

    private static IResult Error(int status, string message, string type)
        => Results.Json(new { error = new { message, type } }, ApiHttp.JsonOptions, statusCode: status);

    private static IResult ErrorFor(Exception e)
    {
        var status = ApiHttp.StatusFor(e);

        var type = status switch
        {
            404   => "not_found",
            429   => "rate_limit_error",
            < 500 => "invalid_request_error",
            _     => "api_error",
        };

        return Error(status, e.Message, type);
    }

    private static async Task ModelActionAsync(string spec, HttpContext context, GeminiClient client)
    {
        var colon  = spec.LastIndexOf(':');
        var model  = colon < 0 ? spec : spec[..colon];
        var action = colon < 0 ? "" : spec[(colon + 1)..];

        switch (action)
        {
            case "generateContent":
                await GenerateContentAsync(model, context, client);
                break;
            case "streamGenerateContent":
                await StreamGenerateContentAsync(model, context, client);
                break;
            case "predictLongRunning":
                await (await PredictLongRunningAsync(model, context, client)).ExecuteAsync(context);
                break;
            default:
                await Error(404, $"Cannot POST {context.Request.Path}", "not_found").ExecuteAsync(context);
                break;
        }
    }

    // ---------------------------------------------------------------- generateContent

    internal sealed record GenerateRequest(string Prompt, List<InputFile> Files, List<ToolBridge.Tool> Tools);

    private static JsonElement Either(JsonElement obj, string snake, string camel)
    {
        var value = obj.Prop(snake);
        return value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? obj.Prop(camel) : value;
    }

    internal static GenerateRequest ParseRequest(JsonElement body)
    {
        var builder = new StringBuilder();
        var system  = Either(body, "system_instruction", "systemInstruction");

        if (system.ValueKind == JsonValueKind.Object)
        {
            var text = string.Join(" ", system.Array("parts").Select(p => p.Str("text")).Where(t => t.Length > 0)).Trim();

            if (text.Length > 0) builder.Append("System: ").Append(text).Append("\n\n");
        }

        var files       = new List<InputFile>();
        var contents    = body.Array("contents").ToList();
        var isMultiTurn = contents.Count > 1;

        foreach (var content in contents)
        {
            var rolePrefix = !isMultiTurn ? "" : content.Str("role").Trim().ToLowerInvariant() switch
            {
                "model" or "assistant" => "Model: ",
                "system"               => "System: ",
                _                      => "User: ",
            };

            var index = 0;

            foreach (var part in content.Array("parts"))
            {
                index++;

                var text = part.Str("text");

                if (text.Length > 0)
                {
                    builder.Append(rolePrefix).Append(text).Append('\n');
                    rolePrefix = "";
                }

                var inline = Either(part, "inline_data", "inlineData");
                var data   = inline.Str("data");

                if (data.Length == 0) continue;

                if (!ChatMessages.TryDecodeBase64(data, out var bytes)) throw new ApiException(400, "decode inline_data: invalid base64 data");

                var mimeType = inline.Str("mimeType");

                if (mimeType.Length == 0) mimeType = inline.Str("mime_type");

                files.Add(new InputFile($"inline_{index}{MediaUrls.ExtensionForMimeType(mimeType)}", mimeType, bytes));
            }
        }

        var prompt = builder.ToString().Trim();

        if (prompt.Length == 0 && files.Count == 0) throw new ApiException(400, "empty content");

        if (prompt.Length == 0) prompt = $"[{files.Count} file(s) attached]";

        var tools = new List<ToolBridge.Tool>();

        foreach (var tool in body.Array("tools"))
        {
            var declarations = Either(tool, "function_declarations", "functionDeclarations");

            if (declarations.ValueKind != JsonValueKind.Array) continue;

            foreach (var fn in declarations.EnumerateArray())
            {
                var parameters = fn.Prop("parameters");
                tools.Add(new ToolBridge.Tool(fn.Str("name"), fn.Str("description"), parameters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : parameters.GetRawText()));
            }
        }

        if (tools.Count > 0) prompt = ToolBridge.GeminiPrompt(tools, prompt);

        return new GenerateRequest(prompt, files, tools);
    }

    private static JsonObject Candidate(JsonArray parts, string? finishReason)
    {
        var candidate = new JsonObject { ["index"] = 0, ["content"] = new JsonObject { ["role"] = "model", ["parts"] = parts } };

        if (finishReason is not null) candidate["finishReason"] = finishReason;

        return candidate;
    }

    private static JsonObject Response(JsonObject candidate, bool usage = true)
    {
        var response = new JsonObject { ["candidates"] = new JsonArray(candidate) };

        if (usage) response["usageMetadata"] = new JsonObject { ["promptTokenCount"] = 0, ["candidatesTokenCount"] = 0, ["totalTokenCount"] = 0 };

        return response;
    }

    internal static async Task<JsonObject> GenerateAsync(string model, GenerateRequest request, GeminiClient client, CancellationToken ct)
    {
        if (Music.IsMusicModel(model))
        {
            var music    = await client.GenerateMusicAsync(request.Prompt, model, new MusicConfig(), ct);
            var mimeType = string.IsNullOrEmpty(music.Audio.MimeType) ? "audio/mpeg" : music.Audio.MimeType;
            var parts    = new JsonArray();

            if (music.Text.Trim().Length > 0) parts.Add(new JsonObject { ["text"] = music.Text.Trim() });

            parts.Add(new JsonObject { ["inlineData"] = InlineData(mimeType, music.Data, music.Audio.FileName) });

            return Response(Candidate(parts, "STOP"));
        }

        var response     = await client.GenerateContentAsync(request.Prompt, new GenerateOptions { Model = model, InputFiles = request.Files }, ct);
        var result       = new JsonArray();
        var finishReason = "STOP";

        if (response.ReasoningText.Length > 0) result.Add(new JsonObject { ["text"] = response.ReasoningText, ["thought"] = true });

        if (request.Tools.Count > 0)
        {
            var (calls, text) = ToolBridge.ParseGemini(response.Text);

            if (calls.Count > 0)
            {
                foreach (var call in calls)
                {
                    var args = call.Args.ValueKind is JsonValueKind.Undefined ? null : JsonNode.Parse(call.Args.GetRawText());
                    result.Add(new JsonObject { ["functionCall"] = new JsonObject { ["name"] = call.Name, ["args"] = args } });
                }

                finishReason = "FUNCTION_CALL";
            }
            else
            {
                result.Add(new JsonObject { ["text"] = text });
            }
        }
        else
        {
            if (response.Text.Length > 0) result.Add(new JsonObject { ["text"] = response.Text });

            foreach (var image in response.Images)
            {
                result.Add(new JsonObject { ["fileData"] = new JsonObject { ["mimeType"] = image.MimeType, ["fileUri"] = image.Url } });
            }

            // Canvas files ("-canvas" models) are returned as inline file parts.
            foreach (var canvas in Canvas.ToFiles(response.Canvases) ?? [])
            {
                result.Add(new JsonObject { ["inlineData"] = InlineData(canvas.MimeType, Encoding.UTF8.GetBytes(canvas.Content), canvas.FileName) });
            }

            if (result.Count == 0) result.Add(new JsonObject { ["text"] = "" });
        }

        return Response(Candidate(result, finishReason));
    }

    private static JsonObject InlineData(string mimeType, byte[] data, string? displayName)
    {
        var inline = new JsonObject { ["mimeType"] = mimeType, ["data"] = Convert.ToBase64String(data) };

        if (!string.IsNullOrEmpty(displayName)) inline["displayName"] = displayName;

        return inline;
    }

    private static async Task<GenerateRequest?> ReadGenerateRequestAsync(string model, HttpContext context, GeminiClient client)
    {
        try
        {
            var request = ParseRequest(await ApiHttp.ReadJsonAsync(context));
            client.ResolveModel(model);
            return request;
        }
        catch (Exception e) when (e is ApiException or GeminiException)
        {
            await ErrorFor(e).ExecuteAsync(context);
            return null;
        }
    }

    private static async Task GenerateContentAsync(string model, HttpContext context, GeminiClient client)
    {
        if (await ReadGenerateRequestAsync(model, context, client) is not { } request) return;

        using var deadline = ApiHttp.Deadline(context, TimeSpan.FromMinutes(5));

        try
        {
            await ApiHttp.WriteJsonAsync(context, 200, await GenerateAsync(model, request, client, deadline.Token));
        }
        catch (Exception e) when (e is ApiException or GeminiException or HttpRequestException)
        {
            await ErrorFor(e).ExecuteAsync(context);
        }
    }

    /// <summary>NDJSON by default; server-sent events with <c>?alt=sse</c> (what google-genai uses).</summary>
    private static async Task StreamGenerateContentAsync(string model, HttpContext context, GeminiClient client)
    {
        if (await ReadGenerateRequestAsync(model, context, client) is not { } request) return;

        var sse = context.Request.Query["alt"] == "sse";

        if (sse)
        {
            ApiHttp.StartEventStream(context);
        }
        else
        {
            context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            context.Response.ContentType = "application/x-ndjson";
        }

        using var deadline = ApiHttp.Deadline(context, TimeSpan.FromMinutes(5));
        var ct = deadline.Token;

        async Task Send(JsonNode payload)
        {
            if (sse)
            {
                await ApiHttp.WriteDataAsync(context, payload.ToJsonString(ApiHttp.JsonOptions), ct);
            }
            else
            {
                await context.Response.WriteAsync(payload.ToJsonString(ApiHttp.JsonOptions) + "\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }

        try
        {
            var response = await GenerateAsync(model, request, client, ct);
            var parts    = (JsonArray)response["candidates"]![0]!["content"]!["parts"]!;

            if (parts.Any(p => p?["functionCall"] is not null))
            {
                await Send(response);
                return;
            }

            foreach (var part in parts)
            {
                var text = part?["text"]?.GetValue<string>();

                if (string.IsNullOrEmpty(text)) continue;

                var thought = part!["thought"]?.GetValue<bool>() ?? false;

                foreach (var piece in TextUtils.SplitIntoChunks(text))
                {
                    var chunk = new JsonObject { ["text"] = piece };

                    if (thought) chunk["thought"] = true;

                    await Send(Response(Candidate(new JsonArray(chunk), null), usage: false));
                    await Task.Delay(ApiHttp.ChunkDelay, ct);
                }
            }

            // The final STOP chunk carries the file parts (canvases, images, audio) whole.
            var fileParts = parts.Where(p => p?["inlineData"] is not null || p?["fileData"] is not null).Select(p => p!.DeepClone()).ToArray();
            var final     = new JsonObject { ["index"] = 0, ["finishReason"] = "STOP" };

            if (fileParts.Length > 0) final["content"] = new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(fileParts) };

            await Send(new JsonObject { ["candidates"] = new JsonArray(final) });
        }
        catch (Exception e) when (e is ApiException or GeminiException or HttpRequestException)
        {
            await Send(new JsonObject { ["error"] = new JsonObject { ["message"] = e.Message, ["type"] = "api_error" } });
        }
    }

    // ---------------------------------------------------------------- video operations

    private static string OperationName(string model, string id)
        => "models/" + (model.StartsWith("models/", StringComparison.Ordinal) ? model[7..] : model) + "/operations/" + id;

    private static async Task<IResult> PredictLongRunningAsync(string model, HttpContext context, GeminiClient client)
    {
        try
        {
            var body      = await ApiHttp.ReadJsonAsync(context);
            var instances = body.Array("instances").ToList();

            if (instances.Count != 1) throw new VideoValidationException("exactly one instance is required");

            var instance = instances[0];

            if (instance.Prop("image").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) || instance.Prop("video").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                throw new VideoValidationException("image and video inputs are not supported; only text-to-video is available");

            var parameters = body.Prop("parameters");

            if (parameters.Number("sampleCount") > 1) throw new VideoValidationException("only one video per request is supported (sampleCount must be 1)");

            var job = client.StartVideoJob(instance.Str("prompt"), model, Video.ParseAspect("", parameters.Str("aspectRatio")));

            return Results.Json(new JsonObject { ["name"] = OperationName(model, job.Id), ["done"] = false }, ApiHttp.JsonOptions);
        }
        catch (Exception e) when (e is ApiException or GeminiException)
        {
            return ErrorFor(e);
        }
    }

    private static IResult GetOperation(string model, string operation, HttpContext context, GeminiClient client)
    {
        VideoJob job;

        try
        {
            job = client.VideoJobs.Get(operation);
        }
        catch (GeminiException e)
        {
            return ErrorFor(e);
        }

        var result = new JsonObject { ["name"] = OperationName(model, job.Id), ["done"] = job.Status != VideoJobStatus.IN_PROGRESS };

        if (job.ConversationId.Length > 0) result["metadata"] = new JsonObject { ["conversationId"] = job.ConversationId };

        switch (job.Status)
        {
            case VideoJobStatus.COMPLETED:
                var fileBase = $"{context.Request.Scheme}://{context.Request.Host}/gemini/v1beta/files";
                var mimeType = string.IsNullOrEmpty(job.Video.MimeType) ? "video/mp4" : job.Video.MimeType;

                result["response"] = new JsonObject
                {
                    ["@type"]                 = "type.googleapis.com/google.ai.generativelanguage.v1beta.PredictLongRunningResponse",
                    ["generateVideoResponse"] = new JsonObject
                    {
                        ["generatedSamples"] = new JsonArray(new JsonObject
                        {
                            ["video"] = new JsonObject { ["uri"] = $"{fileBase}/{job.Id}:download?alt=media", ["encoding"] = mimeType },
                        }),
                    },
                };
                break;

            case VideoJobStatus.FAILED:
                var (code, status) = job.Error?.Code switch
                {
                    MediaGenerationException.QUOTA   => (8, "RESOURCE_EXHAUSTED"),
                    MediaGenerationException.REFUSED => (3, "INVALID_ARGUMENT"),
                    MediaGenerationException.TIMEOUT => (4, "DEADLINE_EXCEEDED"),
                    _                                => (13, "INTERNAL"),
                };

                result["error"] = new JsonObject { ["code"] = code, ["message"] = job.Error?.Message ?? "video generation failed", ["status"] = status };
                break;
        }

        return Results.Json(result, ApiHttp.JsonOptions);
    }

    private static IResult DownloadFile(HttpContext context, GeminiClient client)
    {
        var matches = VideoFileIdRegex().Matches(context.Request.Path.Value ?? "");

        if (matches.Count == 0) return Error(404, "file not found", "not_found");

        var id = matches[^1].Groups[1].Value;

        try
        {
            var (job, data) = client.VideoJobs.Content(id);
            return Results.File(data, string.IsNullOrEmpty(job.Video.MimeType) ? "video/mp4" : job.Video.MimeType, id + ".mp4");
        }
        catch (GeminiException e)
        {
            return ErrorFor(e);
        }
    }

    // ---------------------------------------------------------------- deep research

    internal sealed record ResearchRequest(string Query, DeepResearchOptions Options);

    private static ResearchRequest ParseResearch(JsonElement body, string queryField)
    {
        var query = body.Str(queryField);

        if (query.Length == 0) throw new ApiException(400, $"{queryField} field is required");

        var options = new DeepResearchOptions { Model = body.Str("model") };

        if (body.Str("language") is { Length: > 0 } language) options.Language = language;
        if (body.Number("max_sources") is > 0 and var max) options.MaxSources = (int)max!;

        var attachments = body.Array("images").Select(i => new Attachment(i.Str("name"), i.Str("mime_type"), i.Str("data")));
        options.InputFiles = ChatMessages.ToInputFiles(attachments);

        return new ResearchRequest(query, options);
    }

    private static JsonObject? StepJson(StepInfo? step) => step is null ? null : new JsonObject
    {
        ["step_number"] = step.StepNumber,
        ["type"]        = step.Type,
        ["description"] = step.Description,
        ["query"]       = step.Query.Length > 0 ? step.Query : null,
        ["result"]      = step.Result.Length > 0 ? step.Result : null,
    };

    private static JsonObject? SourceJson(SourceInfo? source) => source is null ? null : new JsonObject
    {
        ["title"]   = source.Title.Length > 0 ? source.Title : null,
        ["url"]     = source.Url.Length > 0 ? source.Url : null,
        ["snippet"] = source.Snippet.Length > 0 ? source.Snippet : null,
        ["domain"]  = source.Domain.Length > 0 ? source.Domain : null,
    };

    private static JsonArray? SourcesJson(IEnumerable<SourceInfo> sources)
    {
        var array = new JsonArray(sources.Select(s => (JsonNode?)SourceJson(s)).ToArray());
        return array.Count > 0 ? array : null;
    }

    private static JsonArray? StepsJson(IEnumerable<StepInfo> steps)
    {
        var array = new JsonArray(steps.Select(s => (JsonNode?)StepJson(s)).ToArray());
        return array.Count > 0 ? array : null;
    }

    private static JsonObject ResearchJson(DeepResearchResult r, string status) => new()
    {
        ["id"]           = r.Id,
        ["status"]       = status,
        ["query"]        = r.Query,
        ["summary"]      = r.Summary,
        ["sources"]      = SourcesJson(r.Sources),
        ["steps"]        = StepsJson(r.Steps),
        ["model"]        = r.Model,
        ["created_at"]   = r.CreatedAt,
        ["completed_at"] = r.CompletedAt,
        ["duration_ms"]  = r.DurationMs,
    };

    private static async Task<IResult> DeepResearchAsync(HttpContext context, DeepResearch research)
    {
        try
        {
            var request = ParseResearch(await ApiHttp.ReadJsonAsync(context), "query");

            using var deadline = ApiHttp.Deadline(context, TimeSpan.FromMinutes(30));

            var result = await research.RunAsync(request.Query, request.Options, null, deadline.Token);

            return Results.Json(ResearchJson(result!, "completed"), ApiHttp.JsonOptions);
        }
        catch (Exception e) when (e is ApiException or GeminiException or HttpRequestException)
        {
            return ErrorFor(e);
        }
    }

    private static async Task DeepResearchStreamAsync(HttpContext context, DeepResearch research)
    {
        ResearchRequest request;

        try
        {
            request = ParseResearch(await ApiHttp.ReadJsonAsync(context), "query");
        }
        catch (ApiException e)
        {
            await ErrorFor(e).ExecuteAsync(context);
            return;
        }

        ApiHttp.StartEventStream(context);

        using var deadline = ApiHttp.Deadline(context, TimeSpan.FromMinutes(30));
        var ct = deadline.Token;

        try
        {
            await research.RunAsync(request.Query, request.Options, async ev =>
            {
                var payload = new JsonObject
                {
                    ["event"]    = ev.Event,
                    ["message"]  = ev.Message.Length > 0 ? ev.Message : null,
                    ["step"]     = StepJson(ev.Step),
                    ["source"]   = SourceJson(ev.Source),
                    ["result"]   = ev.Result is null ? null : ResearchJson(ev.Result, "completed"),
                    ["progress"] = ev.Progress > 0 ? ev.Progress : null,
                    ["error"]    = ev.Error.Length > 0 ? ev.Error : null,
                };

                await ApiHttp.WriteDataAsync(context, payload.ToJsonString(ApiHttp.JsonOptions), ct);
                return true;
            }, ct);
        }
        catch (Exception e) when (e is GeminiException or HttpRequestException)
        {
            // The research already emitted an "error" event for GeminiExceptions; report transport errors too.
            if (e is HttpRequestException) await ApiHttp.WriteDataAsync(context, new JsonObject { ["event"] = "error", ["error"] = e.Message }.ToJsonString(), ct);
        }
    }

    // ---------------------------------------------------------------- interactions (create + poll)

    private sealed class ResearchTask
    {
        public string              Id        { get; init; } = "";
        public string              Query     { get; init; } = "";
        public long                CreatedAt { get; init; }
        public volatile string     Status = "in_progress";
        public DeepResearchResult? Result;
        public string              Error = "";
    }

    private static readonly ConcurrentDictionary<string, ResearchTask> Tasks = new();

    private static JsonObject TaskJson(ResearchTask task)
    {
        var json = new JsonObject
        {
            ["id"]         = task.Id,
            ["status"]     = task.Status,
            ["query"]      = task.Query,
            ["created_at"] = task.CreatedAt,
            ["error"]      = task.Error.Length > 0 ? task.Error : null,
        };

        if (task.Result is { } r)
        {
            json["outputs"]     = new JsonArray(new JsonObject { ["text"] = r.Summary });
            json["sources"]     = SourcesJson(r.Sources);
            json["steps"]       = StepsJson(r.Steps);
            json["duration_ms"] = r.DurationMs;
        }

        return json;
    }

    private static void PurgeOldTasks()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeSeconds();

        foreach (var (id, task) in Tasks)
        {
            if (task.CreatedAt < cutoff) Tasks.TryRemove(id, out _);
        }
    }

    private static async Task InteractionCreateAsync(HttpContext context, DeepResearch research, ILogger<DeepResearch> log)
    {
        ResearchRequest request;
        JsonElement     body;

        try
        {
            body    = await ApiHttp.ReadJsonAsync(context);
            request = ParseResearch(body, "input");
        }
        catch (ApiException e)
        {
            await ErrorFor(e).ExecuteAsync(context);
            return;
        }

        PurgeOldTasks();

        if (body.Bool("stream"))
        {
            ApiHttp.StartEventStream(context);

            using var deadline = ApiHttp.Deadline(context, TimeSpan.FromMinutes(30));
            var ct = deadline.Token;

            try
            {
                await research.RunAsync(request.Query, request.Options, async ev =>
                {
                    JsonObject response;

                    switch (ev.Event)
                    {
                        case ResearchEventType.STEP:
                        case ResearchEventType.PROGRESS:
                            response = new JsonObject { ["status"] = "in_progress", ["query"] = request.Query };
                            break;
                        case ResearchEventType.RESULT when ev.Result is { } r:
                            response = new JsonObject
                            {
                                ["id"]           = r.Id,
                                ["status"]       = "completed",
                                ["query"]        = request.Query,
                                ["outputs"]      = new JsonArray(new JsonObject { ["text"] = r.Summary }),
                                ["sources"]      = SourcesJson(r.Sources),
                                ["steps"]        = StepsJson(r.Steps),
                                ["duration_ms"]  = r.DurationMs,
                                ["created_at"]   = r.CreatedAt,
                                ["completed_at"] = r.CompletedAt,
                            };
                            break;
                        case ResearchEventType.ERROR:
                            response = new JsonObject { ["status"] = "failed", ["query"] = request.Query, ["error"] = ev.Error };
                            break;
                        case ResearchEventType.DONE:
                            await ApiHttp.WriteDataAsync(context, new JsonObject { ["status"] = "completed", ["query"] = request.Query }.ToJsonString(ApiHttp.JsonOptions), ct);
                            return false;
                        default:
                            return true;
                    }

                    await ApiHttp.WriteDataAsync(context, response.ToJsonString(ApiHttp.JsonOptions), ct);
                    return true;
                }, ct);
            }
            catch (GeminiException)
            {
                // Already reported as a "failed" event.
            }

            return;
        }

        var task = new ResearchTask { Id = "task-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)), Query = request.Query, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };

        Tasks[task.Id] = task;

        _ = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));

            try
            {
                task.Result = await research.RunAsync(request.Query, request.Options, null, timeout.Token);
                task.Status = "completed";
            }
            catch (Exception e)
            {
                log.LogWarning("Background research {Id} failed: {Error}", task.Id, e.Message);
                task.Error  = e.Message;
                task.Status = "failed";
            }
        });

        await ApiHttp.WriteJsonAsync(context, 202, TaskJson(task));
    }

    private static IResult InteractionGet(string id)
    {
        if (!Tasks.TryGetValue(id, out var task)) return Error(404, $"task \"{id}\" not found", "not_found");

        return Results.Json(TaskJson(task), ApiHttp.JsonOptions);
    }
}
