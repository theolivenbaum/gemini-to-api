using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeminiToApi.Configuration;
using GeminiToApi.Gemini;
using GeminiToApi.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GeminiToApi.Tests;

public sealed class ApiEndToEndTests : IAsyncLifetime
{
    private readonly FakeGemini   _gemini = new();
    private WebApplication?       _app;
    private HttpClient            _http = null!;

    private async Task<HttpClient> StartAsync(Action<AppConfig>? configure = null)
    {
        var config = AppConfig.FromValues(new Dictionary<string, string> { ["GEMINI_COOKIES"] = "__Secure-1PSID=a; __Secure-1PSIDTS=b" }, Path.GetTempPath());

        configure?.Invoke(config);

        var client = new GeminiClient(config.Gemini, NullLogger<GeminiClient>.Instance, _gemini);
        client.SetSessionForTesting(new GeminiClient.SessionState("token", "__Secure-1PSID=a", "push", "build", "sid", "en", TestJson.TestModels()));

        _app = Server.Build(config, listen: false, configure: builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.Remove(builder.Services.Single(d => d.ImplementationType == typeof(GeminiSessionService)));
            builder.Services.AddSingleton(client);
        });

        await _app.StartAsync();
        _http = _app.GetTestClient();
        return _http;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private static List<string> SseData(string body) => body.Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => l[6..]).ToList();

    // ---------------------------------------------------------------- host

    [Fact]
    public async Task HealthDocsAndFallback()
    {
        var http = await StartAsync();

        Assert.Equal("ok", (await Json(await http.GetAsync("/health")))["status"]!.GetValue<string>());
        Assert.Contains("api-reference", await http.GetStringAsync("/docs"));
        Assert.Contains("swagger", await http.GetStringAsync("/openapi.json"));

        var missing = await http.GetAsync("/nope");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("Cannot GET /nope", (await Json(missing))["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task ApiKeyIsEnforcedWhenConfigured()
    {
        var http = await StartAsync(c => c.ApiKey = "secret");

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/openai/v1/models")).StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Get, "/openai/v1/models");
        request.Headers.Authorization = new("Bearer", "secret");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(request)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/gemini/v1beta/models?key=secret")).StatusCode);
    }

    // ---------------------------------------------------------------- OpenAI

    [Fact]
    public async Task OpenAIModels()
    {
        var http   = await StartAsync();
        var models = await Json(await http.GetAsync("/openai/v1/models"));

        Assert.Equal(["gemini-flash", "gemini-pro"], models["data"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()));
        Assert.Equal("gemini-pro-canvas", (await Json(await http.GetAsync("/openai/v1/models/gemini-pro-canvas")))["id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/openai/v1/models/unknown")).StatusCode);
    }

    [Fact]
    public async Task OpenAIChatCompletion()
    {
        var http     = await StartAsync();
        var response = await http.PostAsync("/openai/v1/chat/completions", Body("""{"model":"gemini-advanced","messages":[{"role":"system","content":"Be brief"},{"role":"user","content":"Hello!"}]}"""));
        var json     = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("chat.completion", json["object"]!.GetValue<string>());
        Assert.Equal("gemini-pro", json["model"]!.GetValue<string>());
        Assert.Equal("gemini-advanced", json["requested_model"]!.GetValue<string>());
        Assert.Equal("Hello from Gemini", json["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.Equal("stop", json["choices"]![0]!["finish_reason"]!.GetValue<string>());

        var sent  = _gemini.Requests.Single(r => r.Url.AbsolutePath.EndsWith("StreamGenerate"));
        var inner = sent.Inner;

        Assert.Equal("System: Be brief\nUser: Hello!", inner[0]![0]!.GetValue<string>());
        Assert.Equal(3, inner[79]!.GetValue<int>());
        Assert.Equal(1, inner[80]!.GetValue<int>());
        Assert.Equal("token", sent.Form("at"));
        Assert.Contains("2222222222222222", sent.Headers["x-goog-ext-525001261-jspb"]);
        Assert.Equal("__Secure-1PSID=a", sent.Headers["Cookie"]);
    }

    [Fact]
    public async Task OpenAIReasoningEffortSelectsThinkingLevel()
    {
        var http = await StartAsync();

        await http.PostAsync("/openai/v1/chat/completions", Body("""{"model":"gemini-flash","reasoning_effort":"medium","messages":[{"role":"user","content":"x"}]}"""));
        Assert.Equal(4, _gemini.Requests.Single().Inner[80]!.GetValue<int>());

        var bad = await http.PostAsync("/openai/v1/chat/completions", Body("""{"model":"gemini-flash","reasoning_effort":"extreme","messages":[{"role":"user","content":"x"}]}"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task OpenAIRejectsBadRequestsBeforeCallingGemini()
    {
        var http = await StartAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/openai/v1/chat/completions", Body("""{"model":"nope","messages":[{"role":"user","content":"x"}]}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/openai/v1/chat/completions", Body("""{"model":"gemini-pro","messages":[]}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/openai/v1/chat/completions", Body("not json"))).StatusCode);
        Assert.Empty(_gemini.Requests);
    }

    [Fact]
    public async Task OpenAIStreaming()
    {
        var http = await StartAsync();

        _gemini.Reply = "The answer is two, as expected.";

        var response = await http.PostAsync("/openai/v1/chat/completions", Body("""{"model":"gemini-pro","stream":true,"messages":[{"role":"user","content":"What's 1+1?"}]}"""));
        var data     = SseData(await response.Content.ReadAsStringAsync());

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("[DONE]", data[^1]);

        var chunks = data[..^1].Select(d => JsonNode.Parse(d)!).ToList();

        Assert.Equal("assistant", chunks[0]["choices"]![0]!["delta"]!["role"]!.GetValue<string>());
        Assert.Equal(_gemini.Reply, string.Concat(chunks.Select(c => c["choices"]![0]!["delta"]!["content"]?.GetValue<string>())));
        Assert.Equal("stop", chunks[^1]["choices"]![0]!["finish_reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenAIToolCallBridge()
    {
        var http = await StartAsync();

        _gemini.Reply = "```json\n" + """{"tool_calls":[{"name":"get_weather","arguments":{"city": "Paris"}},{"name":"not_listed","arguments":{}}]}""" + "\n```";

        var response = await http.PostAsync("/openai/v1/chat/completions", Body("""
            {"model":"gemini-pro","messages":[{"role":"user","content":"Weather?"}],
             "tools":[{"type":"function","function":{"name":"get_weather","description":"Get weather","parameters":{"type":"object"}}}]}
            """));

        var choice = (await Json(response))["choices"]![0]!;
        var call   = choice["message"]!["tool_calls"]!.AsArray().Single()!;

        Assert.Equal("tool_calls", choice["finish_reason"]!.GetValue<string>());
        Assert.Equal("get_weather", call["function"]!["name"]!.GetValue<string>());
        Assert.Equal("""{"city":"Paris"}""", call["function"]!["arguments"]!.GetValue<string>());

        var prompt = _gemini.Requests.Single().Inner[0]![0]!.GetValue<string>();
        Assert.Contains("- name: get_weather | description: Get weather | parameters: {\"type\":\"object\"}", prompt);
    }

    [Fact]
    public async Task OpenAIImageInputIsUploaded()
    {
        var http = await StartAsync();

        _gemini.Override = r => r.Url.Host == "content-push.googleapis.com" ? Upload(r) : null;

        var png = Convert.ToBase64String([0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3]);

        var response = await http.PostAsync("/openai/v1/chat/completions", Body($$$"""
            {"model":"gemini-pro","messages":[{"role":"user","content":[{"type":"text","text":"What is this?"},{"type":"image_url","image_url":{"url":"data:image/png;base64,{{{png}}}"}}]}]}
            """));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var inner = _gemini.Requests.Single(r => r.Url.AbsolutePath.EndsWith("StreamGenerate")).Inner;
        Assert.Equal("[\"User: What is this?\",0,null,[[[\"/contrib_service/file-id\"],\"image_1.png\"]],null,null,0]", inner[0]!.ToJsonString());
    }

    private static HttpResponseMessage Upload(FakeGemini.Captured request)
    {
        if (request.Method == HttpMethod.Options) return new HttpResponseMessage(HttpStatusCode.OK);

        if (request.Headers["X-Goog-Upload-Command"] == "start")
        {
            var start = new HttpResponseMessage(HttpStatusCode.OK);
            start.Headers.Add("X-Goog-Upload-Url", "https://content-push.googleapis.com/upload/session-1");
            return start;
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("/contrib_service/file-id") };
    }

    // ---------------------------------------------------------------- Claude

    [Fact]
    public async Task ClaudeMessage()
    {
        var http     = await StartAsync();
        var response = await http.PostAsync("/claude/v1/messages", Body("""{"model":"gemini-flash","max_tokens":100,"system":[{"type":"text","text":"Be nice"}],"messages":[{"role":"user","content":"Hi"}]}"""));
        var json     = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("message", json["type"]!.GetValue<string>());
        Assert.Equal("end_turn", json["stop_reason"]!.GetValue<string>());
        Assert.Equal("Hello from Gemini", json["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("System: Be nice\n\nUser: Hi", _gemini.Requests.Single().Inner[0]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task ClaudeStreamingEvents()
    {
        var http = await StartAsync();

        var body   = await (await http.PostAsync("/claude/v1/messages", Body("""{"model":"gemini-flash","max_tokens":100,"stream":true,"messages":[{"role":"user","content":"Hi"}]}"""))).Content.ReadAsStringAsync();
        var events = body.Split('\n').Where(l => l.StartsWith("event: ")).Select(l => l[7..]).Distinct().ToList();

        Assert.Equal(["message_start", "content_block_start", "content_block_delta", "content_block_stop", "message_delta", "message_stop"], events);

        var blockStart = SseData(body).Select(d => JsonNode.Parse(d)!).First(e => e["type"]!.GetValue<string>() == "content_block_start");
        Assert.Equal(0, blockStart["index"]!.GetValue<int>());
    }

    [Fact]
    public async Task ClaudeToolUseBridge()
    {
        var http = await StartAsync();

        _gemini.Reply = """{"status":"tool_use","tool_calls":[{"id":"toolu_1","name":"lookup","input":{"q":"cats"}}]}""";

        var json = await Json(await http.PostAsync("/claude/v1/messages", Body("""
            {"model":"gemini-flash","max_tokens":100,"messages":[{"role":"user","content":"Find cats"}],
             "tools":[{"name":"lookup","description":"Search","input_schema":{"type":"object"}}]}
            """)));

        Assert.Equal("tool_use", json["stop_reason"]!.GetValue<string>());
        Assert.Equal("cats", json["content"]![0]!["input"]!["q"]!.GetValue<string>());
    }

    [Fact]
    public async Task ClaudeModelsAndCountTokens()
    {
        var http = await StartAsync();

        var models = await Json(await http.GetAsync("/claude/v1/models"));
        Assert.Equal("gemini-flash", models["first_id"]!.GetValue<string>());

        var count = await Json(await http.PostAsync("/claude/v1/messages/count_tokens", Body("""{"model":"gemini-flash","messages":[{"role":"user","content":"12345678"}]}""")));
        Assert.Equal(2, count["input_tokens"]!.GetValue<int>());
    }

    // ---------------------------------------------------------------- Gemini

    [Fact]
    public async Task GeminiGenerateContent()
    {
        var http     = await StartAsync();
        var response = await http.PostAsync("/gemini/v1beta/models/gemini-pro:generateContent", Body("""{"systemInstruction":{"parts":[{"text":"Rules"}]},"contents":[{"role":"user","parts":[{"text":"Write a poem"}]}]}"""));
        var json     = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Hello from Gemini", json["candidates"]![0]!["content"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("STOP", json["candidates"]![0]!["finishReason"]!.GetValue<string>());
        Assert.Equal("System: Rules\n\nWrite a poem", _gemini.Requests.Single().Inner[0]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task GeminiStreamGenerateContentSse()
    {
        var http = await StartAsync();

        _gemini.Reply = "A short poem about code and coffee.";

        var body   = await (await http.PostAsync("/gemini/v1beta/models/gemini-flash:streamGenerateContent?alt=sse", Body("""{"contents":[{"parts":[{"text":"Poem"}]}]}"""))).Content.ReadAsStringAsync();
        var chunks = SseData(body).Select(d => JsonNode.Parse(d)!).ToList();

        Assert.Equal(_gemini.Reply, string.Concat(chunks.Select(c => c["candidates"]![0]!["content"]?["parts"]?[0]?["text"]?.GetValue<string>())));
        Assert.Equal("STOP", chunks[^1]["candidates"]![0]!["finishReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task GeminiFunctionCalling()
    {
        var http = await StartAsync();

        _gemini.Reply = """{"status":"call","tool_calls":[{"name":"get_time","arguments":{"tz":"UTC"}}]}""";

        var json = await Json(await http.PostAsync("/gemini/v1beta/models/gemini-pro:generateContent", Body("""
            {"contents":[{"parts":[{"text":"Time?"}]}],"tools":[{"functionDeclarations":[{"name":"get_time","parameters":{"type":"object"}}]}]}
            """)));

        var candidate = json["candidates"]![0]!;

        Assert.Equal("FUNCTION_CALL", candidate["finishReason"]!.GetValue<string>());
        Assert.Equal("UTC", candidate["content"]!["parts"]![0]!["functionCall"]!["args"]!["tz"]!.GetValue<string>());
    }

    [Fact]
    public async Task GeminiModelsAndErrors()
    {
        var http = await StartAsync();

        var models = await Json(await http.GetAsync("/gemini/v1beta/models"));
        Assert.Equal("models/gemini-flash", models["models"]![0]!["name"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/gemini/v1beta/models/unknown:generateContent", Body("""{"contents":[{"parts":[{"text":"x"}]}]}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/gemini/v1beta/models/gemini-pro:generateContent", Body("""{"contents":[]}"""))).StatusCode);
    }

    [Fact]
    public async Task GeminiVideoOperationNotFound()
    {
        var http = await StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/gemini/v1beta/models/veo/operations/abc")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/gemini/v1beta/files/abc123:download?alt=media")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/gemini/v1beta/models/veo-3:predictLongRunning", Body("""{"instances":[]}"""))).StatusCode);
    }
}
