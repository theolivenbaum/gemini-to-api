using System.Text.Json;
using GeminiToApi.Api.Common;
using GeminiToApi.Configuration;
using GeminiToApi.Gemini;
using GeminiToApi.Login;

namespace GeminiToApi.Tests;

public class UtilityTests
{
    [Fact]
    public void MergeCookieHeadersPreservesFullSessionAndOverridesAuth()
    {
        var merged = CookieHeader.Merge("NID=1; ", "SID=s; __Secure-1PSID=old; __Secure-1PSIDTS=oldts; OTHER=x", "__Secure-1PSID=new; __Secure-1PSIDTS=newts");

        Assert.Equal("NID=1; SID=s; __Secure-1PSID=new; __Secure-1PSIDTS=newts; OTHER=x", merged);
    }

    [Fact]
    public void ConfigValidationMatchesGo()
    {
        AppConfig Load(Dictionary<string, string> values) => AppConfig.FromValues(values, Path.GetTempPath());

        Assert.Throws<ConfigurationException>(() => Load([]).Validate());
        Assert.Throws<ConfigurationException>(() => Load(new() { ["GEMINI_COOKIES"] = "__Secure-1PSID=a" }).Validate());
        Assert.Throws<ConfigurationException>(() => Load(new() { ["GEMINI_AUTH_USER"] = "-1" }));
        Assert.Throws<ConfigurationException>(() => Load(new() { ["PORT"] = "abc" }));

        var config = Load(new()
        {
            ["GEMINI_COOKIES"]          = "x=1; __Secure-1PSID=a; __Secure-1PSIDTS=b",
            ["GEMINI_AUTH_USER"]        = "2",
            ["GEMINI_REFRESH_INTERVAL"] = "3",
            ["RATE_LIMIT_ENABLED"]      = "true",
        });

        config.Validate();

        Assert.Equal(("a", "b", "2"), (config.Gemini.Secure1PSID, config.Gemini.Secure1PSIDTS, config.Gemini.AuthUser));
        Assert.Equal(AppConfig.MIN_REFRESH_INTERVAL, config.Gemini.RefreshInterval);
        Assert.True(config.RateLimit.Enabled);
        Assert.Equal(AppConfig.DEFAULT_REFRESH_INTERVAL, Load(new() { ["GEMINI_REFRESH_INTERVAL"] = "0" }).Gemini.RefreshInterval);
    }

    [Fact]
    public void DotEnvRoundTripAndPriority()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            DotEnv.Write(AppConfig.CredentialsPath(dir), new Dictionary<string, string> { ["GEMINI_COOKIES"] = "a=\"1\"; b=2", ["GEMINI_AUTH_USER"] = "1" });

            var envFile = Path.Combine(dir, ".env");
            File.WriteAllText(envFile, "# comment\nPORT=5000\nGEMINI_AUTH_USER='3'\nexport LOG_LEVEL=debug # trailing\n");

            Assert.Equal("a=\"1\"; b=2", DotEnv.Read(AppConfig.CredentialsPath(dir))["GEMINI_COOKIES"]);

            var config = AppConfig.Load(new Dictionary<string, string> { ["GEMINI_TO_API_HOME"] = dir, ["PORT"] = "6000" }, envFile, new Dictionary<string, string>());

            Assert.Equal("a=\"1\"; b=2", config.Gemini.Cookies);  // from credentials.env
            Assert.Equal("3", config.Gemini.AuthUser);             // .env wins over credentials.env
            Assert.Equal("debug", config.LogLevel);
            Assert.Equal(6000, config.Port);                       // flags win over everything
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SplitIntoChunksPreservesText()
    {
        const string text = "Hello world, this is a fairly long sentence with\nnew lines\tand tabs. Ünïcødé 🙂 too.";

        var chunks = TextUtils.SplitIntoChunks(text);

        Assert.True(chunks.Count > 3);
        Assert.Equal(text, string.Concat(chunks));
        Assert.Empty(TextUtils.SplitIntoChunks(""));
    }

    [Fact]
    public void ExtractsJsonAndThinking()
    {
        Assert.Equal("{\"a\":\"}\"}", TextUtils.ExtractFirstJsonObject("prefix {\"a\":\"}\"} suffix"));
        Assert.Equal("", TextUtils.ExtractFirstJsonObject("no json"));
        Assert.Equal("{\"x\":1}", TextUtils.StripCodeFence("```json\n{\"x\":1}\n```"));
        Assert.Equal(("thinking", "answer"), TextUtils.ExtractThinkingAndText("<ctrl94>thought thinking<ctrl95>answer"));
        Assert.Equal(("", "plain"), TextUtils.ExtractThinkingAndText("plain"));
    }

    [Fact]
    public void ParsesOpenAIMessages()
    {
        JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        Assert.Equal("[Tool Result for ID call_1]: 42", ChatMessages.ParseOpenAI(Parse("""{"role":"tool","tool_call_id":"call_1","content":"42"}""")).Content);
        Assert.Equal("Thinking\n[Call Tool: add with ID call_1 and Input: {\"a\":1}]",
            ChatMessages.ParseOpenAI(Parse("""{"role":"assistant","content":"Thinking","tool_calls":[{"id":"call_1","type":"function","function":{"name":"add","arguments":"{\"a\":1}"}}]}""")).Content);

        var multimodal = ChatMessages.ParseOpenAI(Parse("""{"role":"user","content":[{"type":"text","text":"Look"},{"type":"image_url","image_url":{"url":"data:image/jpeg;base64,AAAA"}},{"type":"image_url","image_url":{"url":"https://example.com/x.png"}}]}"""));
        Assert.Equal("Look", multimodal.Content);
        Assert.Equal(new Attachment("image_1.jpg", "image/jpeg", "AAAA"), Assert.Single(multimodal.Attachments));
    }

    [Fact]
    public void ParsesAnthropicMessages()
    {
        var message = ChatMessages.ParseAnthropic(JsonDocument.Parse("""
            {"role":"user","content":[
              {"type":"text","text":"Here"},
              {"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}},
              {"type":"tool_use","id":"t1","name":"calc","input":{"x":1}},
              {"type":"tool_result","tool_use_id":"t1","content":[{"type":"text","text":"2"}]}
            ]}
            """).RootElement);

        Assert.Equal("Here\n[Call Tool: calc with ID t1 and Input: {\"x\":1}]\n[Tool Result for ID t1]: 2", message.Content);
        Assert.Equal("image_2.png", Assert.Single(message.Attachments).Name);
    }

    [Fact]
    public void BuildsPrompt()
    {
        var prompt = ChatMessages.BuildPrompt([ChatMessage.Text("user", "Hi"), ChatMessage.Text("assistant", "Hello"), new ChatMessage("user", "", [new Attachment("a", "b", "c")])], "Sys");

        Assert.Equal("System: Sys\n\nUser: Hi\nModel: Hello\nUser: [1 file(s) attached]", prompt);
    }

    [Fact]
    public void DecodesLenientBase64()
    {
        Assert.True(ChatMessages.TryDecodeBase64("aGk", out var raw));
        Assert.Equal("hi", System.Text.Encoding.ASCII.GetString(raw));
        Assert.True(ChatMessages.TryDecodeBase64("-_8=", out var url));
        Assert.Equal([0xfb, 0xff], url);
        Assert.False(ChatMessages.TryDecodeBase64("!!!", out _));
    }

    [Fact]
    public void ToolBridgeParsing()
    {
        var tools = new[] { new ToolBridge.Tool("f", "", "") };

        var (calls, content) = ToolBridge.ParseOpenAI(tools, "", "plain answer");
        Assert.Empty(calls);
        Assert.Equal("plain answer", content);

        (calls, _) = ToolBridge.ParseOpenAI(tools, "", "Sure: {\"tool_calls\":[{\"name\":\"f\",\"arguments\":\"{\\\"a\\\": 1}\"}]}");
        Assert.Equal("{\"a\":1}", Assert.Single(calls).Function.Arguments);

        Assert.Equal("{}", ToolBridge.NormalizeArguments(JsonDocument.Parse("\"not json\"").RootElement));

        var (uses, text) = ToolBridge.ParseClaude("{\"status\":\"text\",\"content\":\"hello\"}");
        Assert.Empty(uses);
        Assert.Equal("hello", text);

        var (fnCalls, _) = ToolBridge.ParseGemini("{\"status\":\"call\",\"tool_calls\":[{\"name\":\"g\",\"arguments\":{}}]}");
        Assert.Equal("g", Assert.Single(fnCalls).Name);
    }

    [Fact]
    public void LoginHelpers()
    {
        Assert.True(CookieCapture.IsCompleteSession("SID=1; __Secure-1PSID=a; __Secure-1PSIDTS=b"));
        Assert.False(CookieCapture.IsCompleteSession("SID=1; __Secure-1PSID=a"));
        Assert.Equal(BrowserKind.Edge, BrowserLocator.ParseKind("msedge"));
        Assert.Equal(BrowserKind.Firefox, BrowserLocator.KindFromPath(@"C:\Program Files\Mozilla Firefox\firefox.exe"));
        Assert.Equal(BrowserKind.Chrome, BrowserLocator.KindFromPath("/opt/chromium/chrome"));
        Assert.Throws<ArgumentException>(() => BrowserLocator.ParseKind("safari"));
    }
}
